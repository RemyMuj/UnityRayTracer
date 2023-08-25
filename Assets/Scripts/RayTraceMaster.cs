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

    private static List<rotBounds> debugBoundsList = new List<rotBounds>();
    
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
    public struct rotBounds {
        public UnityEngine.Matrix4x4 localToWorldMatrix;
        public UnityEngine.Vector3 min;
        public UnityEngine.Vector3 max;
    };

    unsafe public struct SphereList {
        public Sphere unit;
        public SphereList* link;
    };

    unsafe public struct MeshList {
        public MeshObject unit;
        public MeshList* link;
    };

    unsafe public struct SphereBVH {
        public rotBounds bounds;
        public SphereList group;

        public SphereBVH* link1;
        public SphereBVH* link2;
    };

    unsafe public struct MeshBVH {
        public rotBounds bounds;
        public MeshList group;

        public MeshBVH* link1;
        public MeshBVH* link2;
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
        Bounds meshBounds = new Bounds();
        Bounds sphereBounds = new Bounds();

        // Loop over all Ray Trace Objects and gather their data
        foreach (RayTraceObject obj in _rayTraceObjects) {
            // Setup Variables
            RayTraceParams _lighting = new RayTraceParams();

            // Type Specific Setup
            switch(obj.type) {
                case 1: // Handling Spheres
                    // Add contribution to root bounding volume
                    sphereBounds.Encapsulate(obj.bounds);

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
                    // Add contribution to root bounding volume
                    meshBounds.Encapsulate(obj.bounds);

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

        // Return bounds
        Bounds[] return_array = new Bounds[2];
        return_array[0] = meshBounds;
        return_array[1] = sphereBounds;

        return return_array;
    }

    /// ComputeSlicePlane(): Compute Normal Vector for best slicing plane to minimize overlap and maximize 50/50 splitting ///
    private UnityEngine.Matrix4x4 ComputeSlicePlane(List<UnityEngine.Vector3> centerPoints) {
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

        avePoint /= numPoints;
 
        // Project points to best fit plane
        float dist = UnityEngine.Vector3.Dot(xVector, avePoint);

        for (int i = 0; i < numPoints; i++) {
            centerPoints[i] -= dist * xVector;
        }

        // Setup Matrices for finding line of best fit
        float[,] C = new float[numPoints, 3];
        float[,] F = new float[3, 3];
        double[,] E = new double[3, 3];

        for (int i = 0; i < numPoints; i++) {
            A[i, 0] = centerPoints[i].x;
            A[i, 1] = centerPoints[i].y;
            A[i, 2] = centerPoints[i].z;

            C[i, 0] = centerPoints[i].x - avePoint.x;
            C[i, 1] = centerPoints[i].y - avePoint.y;
            C[i, 2] = centerPoints[i].z - avePoint.z;
        }

        F = Matrix.Multiply(Matrix.Transpose(A), C);

        // Finding Eigenvalues and best eigenvector
        for (int i = 0; i < 3; i++) {
            for (int j = 0; j < 3; j++) {
                E[i, j] = (double)(F[i, j]);
            }
        }

        Accord.Math.Decompositions.EigenvalueDecomposition eDecomp = new Accord.Math.Decompositions.EigenvalueDecomposition(E, false, true);

        int index = 0;
        double[] eSolution = new double[3];
        eSolution = eDecomp.RealEigenvalues;
        E = eDecomp.Eigenvectors;

        for (int i = 1; i < 3; i++) {
            if (eSolution[index] < eSolution[i]) {
                index = i;
            }
        }

        eSolution[0] = E[0, index];
        eSolution[1] = E[1, index];
        eSolution[2] = E[2, index];

        // Solve for vector for line of best fit
        UnityEngine.Vector3 yVector = new UnityEngine.Vector3((float) eSolution[0], (float) eSolution[1], (float) eSolution[2]);
        yVector.Normalize();

        // Find third vector for transform matrix
        UnityEngine.Vector3 zVector = UnityEngine.Vector3.Cross(xVector, yVector);
        zVector.Normalize();

        // Setup and return slicingPlane Matrix
        UnityEngine.Matrix4x4 matrix = new UnityEngine.Matrix4x4();

        UnityEngine.Vector4 rightVector = new UnityEngine.Vector4(xVector.x, xVector.y, xVector.z, 0.0f);
        UnityEngine.Vector4 upVector = new UnityEngine.Vector4(yVector.x, yVector.y, yVector.z, 0.0f);
        UnityEngine.Vector4 forwardVector = new UnityEngine.Vector4(zVector.x, zVector.y, zVector.z, 0.0f);
        UnityEngine.Vector4 positionVector = new UnityEngine.Vector4(avePoint.x, avePoint.y, avePoint.z, 1.0f);
        
        matrix.SetColumn(0, rightVector);
        matrix.SetColumn(1, upVector);
        matrix.SetColumn(2, forwardVector);
        matrix.SetColumn(3, positionVector);

        return matrix;
    }

    /// CreateChildBound() ///
    private rotBounds CreateChildBound(UnityEngine.Matrix4x4 sliceMatrix, float checkDis, LayerMask mask) {
        // Setup Lists & Bounds
        List<Sphere> objects = new List<Sphere>();
        Bounds _bound = new Bounds();

        // Boxcast using slicing plane matrix
        Quaternion rot = sliceMatrix.rotation;
        UnityEngine.Vector3 halfExtents = new UnityEngine.Vector3(checkDis * 20.0f, checkDis * 20.0f, 0.0f);
        UnityEngine.Vector3 slicePlaneNormal = new UnityEngine.Vector3(sliceMatrix[1, 0], sliceMatrix[1, 1], sliceMatrix[1, 2]);

        RaycastHit[] hits = Physics.BoxCastAll(sliceMatrix.GetPosition(), halfExtents, slicePlaneNormal, rot, checkDis, mask, QueryTriggerInteraction.Ignore);
        sliceMatrix.SetColumn(3, new UnityEngine.Vector3(0.0f, 0.0f, 0.0f));

        // Index objects found in Boxcast
        foreach (RaycastHit hit in hits) {
            // Find object and corresponding sphere
            RayTraceObject obj = hit.collider.gameObject.GetComponent<RayTraceObject>();
            int index = _rayTraceObjects.FindIndex(x => x == obj); 

            // Find index to edit Sphere
            if (index != -1) {
                index = _rayTraceObjectIndices[index];
            }

            // Add object as child of volume
            if (index != -1) {
                // Add contribution to bounding volume
                _bound.Encapsulate(obj.bounds);

                // Add Object to list
                objects.Add(_spheres[index]);
            }
        }

        // Find Rotated bound extents

        // Create Rotated Bound from regular bound
        rotBounds bound = new rotBounds();
        bound.min = sliceMatrix * _bound.min;
        bound.max = sliceMatrix * _bound.max;

        UnityEngine.Vector3 boxCenter = bound.min + (bound.max / 2);
        UnityEngine.Vector4 posColumn = new UnityEngine.Vector4(boxCenter.x, boxCenter.y, boxCenter.z, 1.0f);

        sliceMatrix.SetColumn(3, posColumn);
        bound.localToWorldMatrix = sliceMatrix;

        // DEBUG: Draw bounding box
        debugBoundsList.Add(bound);

        // Return child bound
        return bound;
    }

    /// SplitSphereBVH(): Recursively split scene into bounding volume hierachy to efficiently search for spheres ///
    private int SplitSphereBVH(rotBounds parent, List<Sphere> spheres) {
        // Setup Variables
        int numObjects = spheres.Count;

        if (numObjects > 1) {
            // Create list of center points
            List<UnityEngine.Vector3> centerPoints = new List<UnityEngine.Vector3>();
            foreach (Sphere sphere in spheres) {
                centerPoints.Add(sphere.position);
            }

            // Get Slicing Plane
            UnityEngine.Matrix4x4 slicePlaneMatrix = ComputeSlicePlane(centerPoints);

            // Setup Boxcasting variables
            float checkDis = (parent.max - parent.min).magnitude;

            // Generate First Child Bounding Box
            rotBounds child1 = CreateChildBound(slicePlaneMatrix, checkDis, LayerMask.GetMask("RayTrace_sphere"));

            // Generate Second Child Bounding Box
            slicePlaneMatrix.SetColumn(1, -1 * slicePlaneMatrix.GetColumn(1));
            rotBounds child2 = CreateChildBound(slicePlaneMatrix, checkDis, LayerMask.GetMask("RayTrace_sphere"));
        }

        // Decide on further iteration or not
        // BLANK

        // Blank Return
        return 1;
    }

    /// RebuildTrees(): Rebuild Bounding Volume Tree for Ray Trace Objects ///
    private void RebuildTrees(Bounds[] rootBounds) {
        // Mesh Tree Rebuilding
        /*
        rotBounds meshRoot = new rotBounds();
        meshRoot.min = rootBounds[0].min;
        meshRoot.max = rootBounds[0].max;
        meshRoot.localToWorldMatrix = UnityEngine.Matrix4x4.identity;
        int treeSize = SplitSphereBVH(meshRoot, _meshObjects);
        //*/

        // Sphere Tree Rebuilding
        rotBounds sphereRoot = new rotBounds();
        sphereRoot.min = rootBounds[1].min;
        sphereRoot.max = rootBounds[1].max;
        sphereRoot.localToWorldMatrix = UnityEngine.Matrix4x4.identity;
        int treeSize = SplitSphereBVH(sphereRoot, _spheres);

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
            ScreenCapture.CaptureScreenshot(Time.time + "-" + _currentSample + ".png");
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
        foreach (rotBounds bound in debugBoundsList) {
            // Change color
            Gizmos.color = new Color(i / maxi, i / maxi, i / maxi);
            i++;

            // Get Transformation Matrix
            UnityEngine.Matrix4x4 mat = bound.localToWorldMatrix;

            // Setup Corners
            UnityEngine.Vector3 corner1 = new UnityEngine.Vector3(bound.max.x, bound.min.y, bound.min.z);
            UnityEngine.Vector3 corner2 = new UnityEngine.Vector3(bound.min.x, bound.max.y, bound.min.z);
            UnityEngine.Vector3 corner3 = new UnityEngine.Vector3(bound.min.x, bound.min.y, bound.max.z);

            UnityEngine.Vector3 corner4 = new UnityEngine.Vector3(bound.min.x, bound.max.y, bound.max.z);
            UnityEngine.Vector3 corner5 = new UnityEngine.Vector3(bound.max.x, bound.min.y, bound.max.z);
            UnityEngine.Vector3 corner6 = new UnityEngine.Vector3(bound.max.x, bound.max.y, bound.min.z);

            // Drawing lines
            Gizmos.DrawLine(mat * bound.min, mat * corner1);
            Gizmos.DrawLine(mat * bound.min, mat * corner2);
            Gizmos.DrawLine(mat * bound.min, mat * corner3);

            Gizmos.DrawLine(mat * bound.max, mat * corner4);
            Gizmos.DrawLine(mat * bound.max, mat * corner5);
            Gizmos.DrawLine(mat * bound.max, mat * corner6);

            Gizmos.DrawLine(mat * corner1, mat * corner5);
            Gizmos.DrawLine(mat * corner1, mat * corner6);

            Gizmos.DrawLine(mat * corner2, mat * corner4);
            Gizmos.DrawLine(mat * corner2, mat * corner6);

            Gizmos.DrawLine(mat * corner3, mat * corner4);
            Gizmos.DrawLine(mat * corner3, mat * corner5);
        }
    }
}