using System.Collections.Generic;
using System.Linq;
using UnityEngine;

//[ExecuteAlways, ImageEffectAllowedInSceneView]
public class RayTraceMaster : MonoBehaviour {
    /// Class Variables (Assigned through Inspector/Engine) ///
    public ComputeShader RayTraceShader;
    private Camera _camera;
    public Texture SkyboxTexture;
    public bool DEBUG = true;
    private RenderTexture _target;
    private RenderTexture _converged;
    private RenderTexture _debug;

    public int numBounces = 8;
    public int numRays = 1;
    private uint _currentSample = 0;
    private Material _additionMaterial;

    private static List<RayTraceObject> _rayTraceObjects = new List<RayTraceObject>();
    private static List<int> _rayTraceObjectIndices = new List<int>();
    private static bool _treesNeedRebuilding = false;

    private static List<MeshObject> _meshObjects = new List<MeshObject>();
    private static List<UnityEngine.Vector3> _vertices = new List<UnityEngine.Vector3>();
    private static List<int> _indices = new List<int>();

    private static List<Sphere> _spheres = new List<Sphere>();

    private static List<Bounds> debugBoundsList = new List<Bounds>();
    
    private ComputeBuffer _meshObjectBuffer;
    private ComputeBuffer _vertexBuffer;
    private ComputeBuffer _indexBuffer;
    private ComputeBuffer _sphereBuffer;

    private static int RayTraceParamsStructSize = 40;
    private static int MeshObjectStructSize = 72 + RayTraceParamsStructSize;
    private static int SphereStructSize = 16 + RayTraceParamsStructSize;
    private static int BVHBaseSize = 0; // Figure this out later
    private static int SphereDepth = 0; // Calculated when figuring out

    /// General Structs ///
    public struct RayTraceParams {
        public UnityEngine.Vector3 color_albedo;
        public UnityEngine.Vector3 color_specular;
        public UnityEngine.Vector3 emission;
        public float smoothness;
    }

    public struct Sphere {
        public UnityEngine.Vector3 position;
        public float radius;
        public RayTraceParams lighting;
    };

    public struct MeshObject {
        public UnityEngine.Matrix4x4 localToWorldMatrix;
        public int indices_offset;
        public int indices_count;
        public RayTraceParams lighting;
    };

    public struct BVHNode {
        public Bounds bounds;
        public int index;
    };

    // Bounding Volume Trees
    List<BVHNode> SphereBVH = new List<BVHNode>();
    List<BVHNode> MeshBVH = new List<BVHNode>(); // UNUSED FOR NOW

    /// OnEnable(): loads in the scene ///
    private void OnEnable() {
        _currentSample = 0;
    }

    /// OnDisable(): Releases buffers ///
    private void OnDisable() {
        if (_sphereBuffer != null) {
            _sphereBuffer.Release();
        }
        
        if (_meshObjectBuffer != null) {
            _meshObjectBuffer.Release();
            _vertexBuffer.Release();
            _indexBuffer.Release();
        }
    }

    /// RegisterObject() ///
    public static void RegisterObject(RayTraceObject obj) {
        // Save to overall list
        _rayTraceObjects.Add(obj);

        // Signal Rebuilding
        _treesNeedRebuilding = true;
    }

    /// UnregisterObject() ///
    public static void UnregisterObject(RayTraceObject obj) {
        // Save to overall list
        _rayTraceObjects.Remove(obj);

        // Signal Rebuilding
        _treesNeedRebuilding = true;
    }

    /// CreateComputeBuffer() ///
    private static void CreateComputeBuffer<T>(ref ComputeBuffer buffer, List<T> data, int stride) where T : struct {
        // Do we already have a compute buffer?
        if (buffer != null) {
            // If no data or buffer doesn't match the given criteria, release it
            if (data.Count == 0 || buffer.count != data.Count || buffer.stride != stride) {
                buffer.Release();
                buffer = null;
            }
        }

        if (data.Count != 0) {
            // If the buffer has been released or wasn't there to
            // begin with, create it
            if (buffer == null) {
                buffer = new ComputeBuffer(data.Count, stride);
            }
            // Set data on the buffer
            buffer.SetData(data);
        }
    }

    /// SetComputeBuffer() /// 
    private void SetComputeBuffer(string name, ComputeBuffer buffer) {
        if (buffer != null) {
            RayTraceShader.SetBuffer(0, name, buffer);
        }
    }

    /// RebuildObjectLists(): Rebuild All Ray Trace Object lists and compile data for tree building ///
    private Bounds[] RebuildObjectLists() {
        //  & Clearing Lists
        _rayTraceObjectIndices.Clear();
        _meshObjects.Clear();
        _vertices.Clear();
        _indices.Clear();
        _spheres.Clear();

        // Setup New Scene bounds
        List<Bounds> MeshBoundsList = new List<Bounds>();
        List<Bounds> SphereBoundsList = new List<Bounds>();

        // Loop over all Ray Trace Objects and gather their data
        foreach (RayTraceObject obj in _rayTraceObjects) {
            // Setup Variables
            RayTraceParams _lighting = new RayTraceParams();

            // Type Specific Setup
            switch(obj.type) {
                case 1: // Handling Spheres
                    // Keep Bounds
                    SphereBoundsList.Add(obj.bounds);

                    // Add Lighting Parameters
                    _lighting.color_albedo = new UnityEngine.Vector3(obj.albedoColor.r, obj.albedoColor.g, obj.albedoColor.b);
                    _lighting.color_specular = new UnityEngine.Vector3(obj.specularColor.r, obj.specularColor.g, obj.specularColor.b);
                    _lighting.emission = new UnityEngine.Vector3(obj.emissionColor.r, obj.emissionColor.g, obj.emissionColor.b);
                    _lighting.smoothness = obj.smoothness;

                    // Add to list
                    _spheres.Add(new Sphere() {
                        radius = obj.radius,
                        position = obj.position,
                        lighting = _lighting
                    });

                    // Save index for sphere list
                    _rayTraceObjectIndices.Add(_spheres.Count - 1);
                    break;

                default: // Handling Meshes
                    // Keep Bounds
                    MeshBoundsList.Add(obj.bounds);

                    // Add vertex data
                    Mesh mesh = obj.GetComponent<MeshFilter>().sharedMesh;
                    int firstVertex = _vertices.Count;
                    _vertices.AddRange(mesh.vertices);

                    // Add index data - if the vertex buffer wasn't empty before, the indices need to be offset
                    int firstIndex = _indices.Count;
                    var indices = mesh.GetIndices(0);
                    _indices.AddRange(indices.Select(index => index + firstVertex));

                    // Add Lighting Parameters
                    _lighting.color_albedo = new UnityEngine.Vector3(obj.albedoColor.r, obj.albedoColor.g, obj.albedoColor.b);
                    _lighting.color_specular = new UnityEngine.Vector3(obj.specularColor.r, obj.specularColor.g, obj.specularColor.b);
                    _lighting.emission = new UnityEngine.Vector3(obj.emissionColor.r, obj.emissionColor.g, obj.emissionColor.b);
                    _lighting.smoothness = obj.smoothness;

                    // Add the object itself
                    _meshObjects.Add(new MeshObject() {
                        localToWorldMatrix = obj.transform.localToWorldMatrix,
                        indices_offset = firstIndex,
                        indices_count = indices.Length,
                        lighting = _lighting
                    });

                    // Save index for mesh list
                    _rayTraceObjectIndices.Add(_meshObjects.Count - 1);
                    break;
            }
        }

        // Encapsulate Bounds
        Bounds[] return_array = new Bounds[2];

        if (MeshBoundsList.Count >= 1) {
            Bounds meshBounds = MeshBoundsList[0];

            foreach (Bounds bound in MeshBoundsList) {
                meshBounds.Encapsulate(bound);
            }

            return_array[0] = meshBounds;
        } else {
            return_array[0] = new Bounds();
        }

        if (SphereBoundsList.Count >= 1) {
            Bounds sphereBounds = SphereBoundsList[0];

            foreach (Bounds bound in SphereBoundsList) {
                sphereBounds.Encapsulate(bound);
            }

            return_array[1] = sphereBounds;
        } else {
            return_array[1] = new Bounds();
        }

        // Return bounds
        return return_array;
    }

    /// ComputeCenter(): Compute center of objects in bounding box to maximize 50/50 splitting ///
    private UnityEngine.Vector3 ComputeCenter(List<UnityEngine.Vector3> centerPoints) {
        // Variables
        UnityEngine.Vector3 avePoint = new UnityEngine.Vector3(0.0f, 0.0f, 0.0f);
        float numPoints = (float) centerPoints.Count;

        // summing coordinates
        for (int i = 0; i < numPoints; i++) {
            avePoint += centerPoints[i];
        }

        return avePoint / numPoints;
    }

    /// SplitBounds() ///
    private Bounds[] SplitBounds(UnityEngine.Vector3 start, UnityEngine.Vector3 extents, LayerMask mask, int count) {
        // Setup Variables
        float[] counts = {count / 2.0f, count / 2.0f, count / 2.0f};
        int hitIndex = 0;

        // Test Collision boxes along each axis
        Collider[][] FrontHits = new Collider[3][];
        Collider[][] BackHits = new Collider[3][];

        FrontHits[0] = Physics.OverlapBox(start + new UnityEngine.Vector3(extents.x / 2.0f, 0, 0), new UnityEngine.Vector3(extents.x / 2.0f, extents.y, extents.z), Quaternion.identity, mask, QueryTriggerInteraction.Ignore); // x-axis test
        FrontHits[1] = Physics.OverlapBox(start + new UnityEngine.Vector3(0, extents.y / 2.0f, 0), new UnityEngine.Vector3(extents.x, extents.y / 2.0f, extents.z), Quaternion.identity, mask, QueryTriggerInteraction.Ignore); // y-axis test
        FrontHits[2] = Physics.OverlapBox(start + new UnityEngine.Vector3(0, 0, extents.z / 2.0f), new UnityEngine.Vector3(extents.x, extents.y, extents.z / 2.0f), Quaternion.identity, mask, QueryTriggerInteraction.Ignore); // z-axis test

        BackHits[0] = Physics.OverlapBox(start - new UnityEngine.Vector3(extents.x / 2.0f, 0, 0), new UnityEngine.Vector3(extents.x / 2.0f, extents.y, extents.z), Quaternion.identity, mask, QueryTriggerInteraction.Ignore); // x-axis test
        BackHits[1] = Physics.OverlapBox(start - new UnityEngine.Vector3(0, extents.y / 2.0f, 0), new UnityEngine.Vector3(extents.x, extents.y / 2.0f, extents.z), Quaternion.identity, mask, QueryTriggerInteraction.Ignore); // y-axis test
        BackHits[2] = Physics.OverlapBox(start - new UnityEngine.Vector3(0, 0, extents.z / 2.0f), new UnityEngine.Vector3(extents.x, extents.y, extents.z / 2.0f), Quaternion.identity, mask, QueryTriggerInteraction.Ignore); // z-axis test

        // Compare slicing axes
        for (int i = 0; i < 3; i++) {
            counts[i] = Mathf.Abs(counts[i] - FrontHits[i].Length);

            if (Mathf.Abs(counts[i]) <= Mathf.Min(counts)) 
                hitIndex = i;
        }

        // Debug Report
        if (DEBUG) {
            int overlapCount = 0;
            for (int i = 0; i < FrontHits[hitIndex].Length; i++) {
                if (BackHits[hitIndex].Contains(FrontHits[hitIndex][i]))
                    overlapCount++;
            }
            
            Debug.Log(" > Total: " + count + ", Slice Axis: " + hitIndex + ", Front hits: " + FrontHits[hitIndex].Length + ", Backhits: " + BackHits[hitIndex].Length + ", Overlaps: " + overlapCount);
        }

        // Create largest extents for child bounds based on winning axis
        Bounds bound1 = FrontHits[hitIndex][0].bounds;
        Bounds bound2 = BackHits[hitIndex][0].bounds;

        foreach (Collider hit in FrontHits[hitIndex]) {
            // Find object and object index
            RayTraceObject obj = hit.gameObject.GetComponent<RayTraceObject>();
            int index = _rayTraceObjects.FindIndex(x => x == obj); 

            // Encapsulate Object
            if (index != -1) {
                bound1.Encapsulate(obj.bounds);
            }
        }

        foreach (Collider hit in BackHits[hitIndex]) {
            // Find object and object index
            RayTraceObject obj = hit.gameObject.GetComponent<RayTraceObject>();
            int index = _rayTraceObjects.FindIndex(x => x == obj); 

            // Encapsulate Object
            if (index != -1) {
                bound2.Encapsulate(obj.bounds);
            }
        }

        // DEBUG Drawing
        debugBoundsList.Add(bound1);
        debugBoundsList.Add(bound2);

        // Return Child Nodes
        Bounds[] final = new Bounds[2];
        final[0] = bound1;
        final[1] = bound2;

        return final;
    }

    /// GetSpheresInBound() ///
    private List<Sphere> GetSpheresInBound(Bounds bounds) {
        List<Sphere> list = new List<Sphere>();

        foreach (RayTraceObject obj in _rayTraceObjects) {
            if (obj.type == 1 && bounds.Intersects(obj.bounds)) {
                list.Add(_spheres[_rayTraceObjectIndices[_rayTraceObjects.FindIndex(x => x == obj)]]);
            }
        }

        return list;
    }

    /// CreateBVH_Spheres(): Recursively split scene into bounding volume hierachy to efficiently search for spheres ///
    private void CreateBVH_Spheres(Bounds parent, List<Sphere> spheres, int depth, int index, out List<BVHNode> SphereBVH) {
        // Setup Variables
        int numObjects = spheres.Count;
        Bounds[] newBounds = new Bounds[2];

        // Debug Report
        if (DEBUG) {
            Debug.Log("[SPHERES] SplitBounds_Spheres() called on " + numObjects + " object(s)");
        }

        // Search Parent Bound
        if (numObjects > 1 && depth <= SphereDepth) {
            // Create list of center points
            List<UnityEngine.Vector3> centerPoints = new List<UnityEngine.Vector3>();

            foreach (Sphere sphere in spheres) {
                centerPoints.Add(sphere.position);
            }

            // Generate Child Bounding Boxes
            newBounds = SplitBounds(ComputeCenter(centerPoints), parent.extents, LayerMask.GetMask("RayTrace_sphere"), numObjects);
            List<Sphere> list1 = GetSpheresInBound(newBounds[0]);
            List<Sphere> list2 = GetSpheresInBound(newBounds[1]);

            // Debug Report
            if (DEBUG) {
                Debug.Log(" > Child1 List Length = " + list1.Count + ", Child2 List Length = " + list2.Count);
            }

            // Left Child Recursion
            if (list1.Count > 1 && depth < SphereDepth) {
                CreateBVH_Spheres(newBounds[0], list1, depth + 1, index - (SphereDepth - depth), out SphereBVH);
            } else if (list1.Count > 1) {
                // DEBUG Message for now, will figure out how to handle better later !!!!!
                Debug.Log("<WARNING> Overlapping bounds left some spheres out of BVH Tree");
            }

            // Right Child Recursion
            if (list2.Count > 1 && depth < SphereDepth) {
                CreateBVH_Spheres(newBounds[1], list2, depth + 1, index + (SphereDepth - depth), out SphereBVH);
            } else if (list2.Count > 1) {
                // DEBUG Message for now, will figure out how to handle better later !!!!!
                Debug.Log("<WARNING> Overlapping bounds left some spheres out of BVH Tree");
            }
        }

        // Final Value Setting
        SphereBVH[index].bounds = parent;
        if (numObjects > 0) {
            SphereBVH[index].index = _spheres.FindIndex(x => x == spheres[0]);
        }
    }

    /// RebuildTrees(): Rebuild Bounding Volume Tree for Ray Trace Objects ///
    private void RebuildTrees(Bounds[] rootBounds) {
        // Mesh Tree Rebuilding
        /*
        rotBounds meshRoot = new rotBounds();
        meshRoot.min = rootBounds[0].min;
        meshRoot.max = rootBounds[0].max;
        meshRoot.localToWorldMatrix = UnityEngine.Matrix4x4.identity;
        int treeSize = SplitSphereMesh(meshRoot, _meshObjects);
        //*/

        // Sphere BVH Tree Rebuilding
        SphereDepth = Mathf.CeilToInt(Mathf.Log(_spheres.Count)) + 1;
        int SphereLength = (int) Mathf.Round(Mathf.Pow(2.0f, (float) SphereDepth)) - 1;

        for (int i = 0; i < SphereLength; i++) {
            BVHNode newNode = new BVHNode();
            newNode.index = -1;

            SphereBVH.Add(newNode);
        }

        CreateBVH_Spheres(rootBounds[1], _spheres, 1, (int) ((SphereLength - 1) / 2.0f), out SphereBVH);

        // Debug Reporting
        if (DEBUG) {
            Debug.Log("[SPHERES] Depth: " + SphereDepth + ", Length:" + SphereLength);
        }

        // DEBUG Drawing
        debugBoundsList.Add(rootBounds[1]);

        // Update Computer buffers
        CreateComputeBuffer(ref _meshObjectBuffer, _meshObjects, MeshObjectStructSize);
        CreateComputeBuffer(ref _vertexBuffer, _vertices, 12);
        CreateComputeBuffer(ref _indexBuffer, _indices, 4);
        CreateComputeBuffer(ref _sphereBuffer, _spheres, SphereStructSize);
    }

    /// Awake(): Camera setup///
    private void Awake() {
        // Getting Camera
        _camera = GetComponent<Camera>();
    }

    /// Update(): Resets image sampling count for when camera changes view ///
    private void Update() {
        if (Input.GetKeyDown(KeyCode.F12)) {
            ScreenCapture.CaptureScreenshot("Screenshots/" + Time.time + "-" + _currentSample + ".png");
        }

        if (transform.hasChanged) {
            _currentSample = 0;
            transform.hasChanged = false;
        }
    }

    /// SetShaderParameters(): Setup parameters for shader to use
    private void SetShaderParameters() {
        RayTraceShader.SetMatrix("_CameraToWorld", _camera.cameraToWorldMatrix);
        RayTraceShader.SetMatrix("_CameraInverseProjection", _camera.projectionMatrix.inverse);

        RayTraceShader.SetTexture(0, "_SkyboxTexture", SkyboxTexture);
        RayTraceShader.SetVector("_PixelOffset", new Vector2(UnityEngine.Random.value, UnityEngine.Random.value));
        RayTraceShader.SetFloat("_Seed", UnityEngine.Random.value);
        RayTraceShader.SetInt("_numBounces", numBounces);
        RayTraceShader.SetInt("_numRays", numRays);

        SetComputeBuffer("_Spheres", _sphereBuffer);
        SetComputeBuffer("_MeshObjects", _meshObjectBuffer);
        SetComputeBuffer("_Vertices", _vertexBuffer);
        SetComputeBuffer("_Indices", _indexBuffer);
    }

    /// Render(): Initialize and dispatch the computer shader to render the scene ///
    private void Render(RenderTexture destination) {
        // Make sure we have a current render target
        InitRenderTexture();

        // Set the target for the compute shader
        RayTraceShader.SetTexture(0, "Result", _target);

        // setup number of threads in a group per pixel
        int threadGroupsX = Mathf.CeilToInt(Screen.width / 8.0f);
        int threadGroupsY = Mathf.CeilToInt(Screen.height / 8.0f);

        // Dispatch the computer shader
        RayTraceShader.Dispatch(0, threadGroupsX, threadGroupsY, 1);

        // Blit the result texture to the screen (With Addition Sampling)
        if (_additionMaterial == null) {
            _additionMaterial = new Material(Shader.Find("Hidden/AdditionShader"));
        }

        _additionMaterial.SetFloat("_Sample", _currentSample);
        Graphics.Blit(_target, _converged, _additionMaterial);
        Graphics.Blit(_converged, destination);
        _currentSample++;
    }

    /// InitRenderTexture(): Initialize fullscreen render texture ///
    private void InitRenderTexture() {
        if (_target == null || _target.width != Screen.width || _target.height != Screen.height)
        {
            // Release render textures if we already have them
            if (_target != null) {
                _target.Release();
                _converged.Release();
            }

            // Set render target textures for Ray Tracing
            _target = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            _target.enableRandomWrite = true;
            _target.Create();

            _converged = new RenderTexture(Screen.width, Screen.height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            _converged.enableRandomWrite = true;
            _converged.Create();

            // Reset Addition Sampling
            _currentSample = 0;
        }
    }

    /// OnRenderImage(): Automatically called by Unity whenever camera is done rendering through shader ///
    private void OnRenderImage(RenderTexture source, RenderTexture destination) {
        // Update buffers
        if (_treesNeedRebuilding) {
            // Reset Variables
            _currentSample = 0;
            _treesNeedRebuilding = false;

            // Rebuild Buffers
            RebuildTrees(RebuildObjectLists());
        }

        // Update Parameters
        SetShaderParameters();

        // Render
        Render(destination);
    }

    /// OnDrawGizmos(): Debug Drawing ///
    void OnDrawGizmos() {
        // Setup Variables
        float i = 0;
        float maxi = debugBoundsList.Count;

        // Draw each bound
        foreach (Bounds bound in debugBoundsList) {
            // Change color
            Gizmos.color = new Color(i / maxi, 0.5f, 0.5f, 1.0f / maxi);
            i++;

            // Setup Corners
            UnityEngine.Vector3 corner1 = new UnityEngine.Vector3(bound.max.x, bound.min.y, bound.min.z);
            UnityEngine.Vector3 corner2 = new UnityEngine.Vector3(bound.min.x, bound.max.y, bound.min.z);
            UnityEngine.Vector3 corner3 = new UnityEngine.Vector3(bound.min.x, bound.min.y, bound.max.z);

            UnityEngine.Vector3 corner4 = new UnityEngine.Vector3(bound.min.x, bound.max.y, bound.max.z);
            UnityEngine.Vector3 corner5 = new UnityEngine.Vector3(bound.max.x, bound.min.y, bound.max.z);
            UnityEngine.Vector3 corner6 = new UnityEngine.Vector3(bound.max.x, bound.max.y, bound.min.z);

            // Drawing lines
            Gizmos.DrawLine(bound.min, corner1);
            Gizmos.DrawLine(bound.min, corner2);
            Gizmos.DrawLine(bound.min, corner3);

            Gizmos.DrawLine(bound.max, corner4);
            Gizmos.DrawLine(bound.max, corner5);
            Gizmos.DrawLine(bound.max, corner6);

            Gizmos.DrawLine(corner1, corner5);
            Gizmos.DrawLine(corner1, corner6);

            Gizmos.DrawLine(corner2, corner4);
            Gizmos.DrawLine(corner2, corner6);

            Gizmos.DrawLine(corner3, corner4);
            Gizmos.DrawLine(corner3, corner5);
        }
    }
}