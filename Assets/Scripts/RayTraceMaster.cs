using System.Collections.Generic;
using Accord.Math;
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

    /// Bounding Volume Structs ///
    unsafe public struct SphereList {
        public Sphere unit;
        public SphereList* link;
    };

    unsafe public struct MeshList {
        public MeshObject unit;
        public MeshList* link;
    };

    unsafe public struct SphereNode {
        public Bounds bounds;
        public SphereList spheres;

        public SphereNode* link1;
        public SphereNode* link2;
    };

    unsafe public struct MeshNode {
        public Bounds bounds;
        public MeshList meshes;

        public MeshNode* link1;
        public MeshNode* link2;
    };

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

    /// ComputeCenter(): Compute center for bounding boxes to maximize 50/50 splitting ///
    private UnityEngine.Vector3 ComputeCenter(List<UnityEngine.Vector3> centerPoints) {
        // Setup Matrices
        int numPoints = centerPoints.Count;
        float[,] A = new float[numPoints, 3];
        float[,] B = new float[numPoints, 1];

        for (int i = 0; i < numPoints; i++) {
            A[i, 0] = centerPoints[i].x;
            A[i, 1] = centerPoints[i].y;
            A[i, 2] = 1;

            B[i, 0] = centerPoints[i].z;
        }

        // Solve for normal vector of plane of best fit
        float[,] solution = Matrix.Solve(A, B, true);
        UnityEngine.Vector3 xVector = new UnityEngine.Vector3(solution[0, 0], solution[1, 0], solution[2, 0]);
        xVector.Normalize();

        // Compute average point
        UnityEngine.Vector3 avePoint = new UnityEngine.Vector3(0.0f, 0.0f, 0.0f);

        for (int i = 0; i < numPoints; i++) {
            avePoint += centerPoints[i];
        }

        return avePoint / numPoints;
    }

    /// FindChildBounds() ///
    private Bounds[] FindChildBounds(UnityEngine.Vector3 start, UnityEngine.Vector3 extents, int objType, int count) {
        // Setup Lists & Bounds
        LayerMask mask;

        switch(objType) {
            // Handling Spheres
            case 1: mask = LayerMask.GetMask("RayTrace_sphere"); break;

            // Handling Meshes (WIP)
            default: mask = LayerMask.GetMask("RayTrace_mesh"); break;
        }

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
            
            Debug.Log("Total: " + count + ", Slice Axis: " + hitIndex + ", Front hits: " + FrontHits[hitIndex].Length + ", Backhits: " + BackHits[hitIndex].Length + ", Overlaps: " + overlapCount);
        }

        // Create largest extents for child bounds based on winning axis
        Bounds[] boxes = new Bounds[2];
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

        // Return child bound
        boxes[0] = bound1;
        boxes[1] = bound2;
        return boxes;
    }

    /// CreateSphereList(): Create list of spheres that fall in given AABB.
    private List<Sphere> CreateSphereList(Bounds bound) {
        // Starting Variable
        List<Sphere> objects = new List<Sphere>();

        // Find colliding Spheres
        LayerMask mask = LayerMask.NameToLayer("RayTrace_sphere");
        Collider[] hits = Physics.OverlapBox(bound.center, bound.extents, Quaternion.identity, mask, QueryTriggerInteraction.Ignore);
        Debug.Log("Num Spheres in List: " + hits.Length + ", Bounds size: " + bound.extents.magnitude);

        // Convert Colliders to Spheres        
        foreach (Collider hit in hits) {
            RayTraceObject obj = hit.gameObject.GetComponent<RayTraceObject>();
            int index = _rayTraceObjects.FindIndex(x => x == obj); 

            // Add to list
            if (index != -1) {
                index = _rayTraceObjectIndices[index];
            }

            if (index != -1) {
                objects.Add(_spheres[index]);
            }
        }

        return objects;
    }

    /// CreateSphereLinkedList(): Create linked list of spheres that fall in given AABB
    private SphereList CreateSphereLinkedList(List<Sphere> objects) {
        // Starting list variables
        SphereList list = new SphereList();
        SphereList list_prev;
        SphereList list_next;

        // Building linked list
        unsafe {
            list_prev = list;
            list_prev.link = null;

            if (objects.Count >= 1) {
                list_prev.unit = objects[0];
            }

            for (int i = 1; i < objects.Count; i++) {
                list_next = new SphereList();
                list_next.unit = objects[i];
                list_next.link = null;

                list_prev.link = &list_next;
                list_prev = list_next;
            }
        }

        return list;
    }

    /// SplitSphereBounds(): Recursively split scene into bounding volume hierachy to efficiently search for spheres ///
    unsafe private SphereNode*[] SplitSphereBounds(SphereNode parent, List<Sphere> spheres) {
        // Setup Variables
        int numObjects = spheres.Count;

        SphereNode[] nodes = new SphereNode[2];
        nodes[0] = new SphereNode();
        nodes[1] = new SphereNode();

        SphereNode*[] ptrs = new SphereNode*[2];
        ptrs[0] = null;
        ptrs[1] = null;

        // Debug Report
        if (DEBUG) {
            Debug.Log("SplitSphereBounds() called on " + numObjects + " object(s)");
        }

        // Search Parent Bound
        if (numObjects > 1) {
            // Create list of center points
            List<UnityEngine.Vector3> centerPoints = new List<UnityEngine.Vector3>();
            foreach (Sphere sphere in spheres) {
                centerPoints.Add(sphere.position);
            }

            // Setup variables
            UnityEngine.Vector3 start = ComputeCenter(centerPoints);

            // Generate Child Bounding Boxes
            Bounds[] childBounds = new Bounds[2];
            childBounds = FindChildBounds(start, parent.bounds.extents, 1, numObjects);

            List<Sphere>[] childLists = new List<Sphere>[2];
            childLists[0] = CreateSphereList(childBounds[0]);
            childLists[1] = CreateSphereList(childBounds[1]);

            // Stop recursion if splitting was unuseful
            if (numObjects == childLists[0].Count || numObjects == childLists[1].Count) {
                return ptrs;
            }
            
            // Create children
            SphereNode*[] links = new SphereNode*[2];

            nodes[0].bounds = childBounds[0];
            nodes[0].spheres = CreateSphereLinkedList(childLists[0]);
            links = SplitSphereBounds(nodes[0], childLists[0]);
            nodes[0].link1 = links[0];
            nodes[0].link2 = links[1];

            nodes[1].bounds = childBounds[1];
            nodes[1].spheres = CreateSphereLinkedList(childLists[1]);
            links = SplitSphereBounds(nodes[1], childLists[1]);
            nodes[1].link1 = links[0];
            nodes[1].link2 = links[1];
        }

        // Default Return
        fixed (SphereNode* ptr1 = &(nodes[0])) {ptrs[0] = ptr1;}
        fixed (SphereNode* ptr2 = &(nodes[1])) {ptrs[1] = ptr2;}
        return ptrs;
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

        // Sphere Tree Rebuilding
        SphereNode sphereRoot = new SphereNode();
        sphereRoot.spheres = CreateSphereLinkedList(_spheres);
        sphereRoot.bounds.min = rootBounds[1].min;
        sphereRoot.bounds.max = rootBounds[1].max;
        unsafe {
            SphereNode*[] links = SplitSphereBounds(sphereRoot, _spheres);
            sphereRoot.link1 = links[0];
            sphereRoot.link2 = links[1];
        }

        // DEBUG Drawing
        debugBoundsList.Add(sphereRoot.bounds);

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