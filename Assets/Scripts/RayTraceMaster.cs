using System.Collections.Generic;
using System.Linq;
using UnityEngine;

//[ExecuteAlways, ImageEffectAllowedInSceneView]
public class RayTraceMaster : MonoBehaviour {
    /// Class Variables ///
    public ComputeShader RayTraceShader;
    private Camera _camera;
    public Texture SkyboxTexture;
    private RenderTexture _target;
    private RenderTexture _converged;
    private RenderTexture _debug;
    private int DEBUG_LEVEL = 2; // 0 - NONE, 1 - DETAILED, 2 - BASIC, 3 - WARNINGS ONLY

    public int numBounces = 8; 
    public int numRays = 1;
    private uint _currentSample = 0;
    private Material _additionMaterial;

    private static List<RayTraceObject> _rayTraceObjects = new List<RayTraceObject>();
    private static List<int> _rayTraceObjectIndices = new List<int>();
    private static bool _treesNeedRebuilding = false;

    private static List<MeshObject> _meshObjects = new List<MeshObject>();
    private static List<Vector3> _vertices = new List<Vector3>();
    private static List<int> _indices = new List<int>();
    private static List<Vector3> _normals = new List<Vector3>();

    private static List<Sphere> _spheres = new List<Sphere>();
    
    private ComputeBuffer _meshObjectBuffer;
    private ComputeBuffer _vertexBuffer;
    private ComputeBuffer _indexBuffer;
    private ComputeBuffer _sphereBuffer;
    private ComputeBuffer _meshObjectBVHBuffer;
    private ComputeBuffer _sphereBVHBuffer;

    private ComputeBuffer _normalBuffer;

    private static int RayTraceParamsStructSize = 40;
    private static int MeshObjectStructSize = 72 + RayTraceParamsStructSize;
    private static int SphereStructSize = 16 + RayTraceParamsStructSize;
    private static int BVHNodeSize = 28;
    
    /// General Structs ///
    public struct RayTraceParams {
        public Vector3 color_albedo;
        public Vector3 color_specular;
        public Vector3 emission;
        public float smoothness;

        public bool Equals(RayTraceParams other) {
            return (other.color_albedo == color_albedo && other.color_specular == color_specular && other.emission == emission && other.smoothness == smoothness);
        }

        public override bool Equals(object obj) {
            return (obj != null && GetType() == obj.GetType() && this.Equals((RayTraceParams) obj));
        }

        public override int GetHashCode() {
            unchecked {
                int hash = 17;
                hash = (hash * 23) + color_albedo.GetHashCode();
                hash = (hash * 23) + color_specular.GetHashCode();
                hash = (hash * 23) + emission.GetHashCode();
                hash = (hash * 23) + smoothness.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(RayTraceParams r1, RayTraceParams r2) {
            return r1.Equals(r2);
        }

        public static bool operator !=(RayTraceParams r1, RayTraceParams r2) {
            return !r1.Equals(r2);
        }
    };

    public struct MeshObject {
        public Matrix4x4 localToWorldMatrix;
        public int indices_offset;
        public int indices_count;
        public RayTraceParams lighting;

        public bool Equals(MeshObject other) {
            return (other.localToWorldMatrix == localToWorldMatrix && other.indices_offset == indices_offset && other.indices_count == indices_count && other.lighting == lighting);
        }

        public override bool Equals(object obj) {
            return (obj != null && GetType() == obj.GetType() && this.Equals((MeshObject) obj));
        }

        public override int GetHashCode() {
            unchecked {
                int hash = 17;
                hash = (hash * 23) + localToWorldMatrix.GetHashCode();
                hash = (hash * 23) + indices_offset.GetHashCode();
                hash = (hash * 23) + indices_count.GetHashCode();
                hash = (hash * 23) + lighting.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(MeshObject m1, MeshObject m2) {
            return m1.Equals(m2);
        }

        public static bool operator !=(MeshObject m1, MeshObject m2) {
            return !m1.Equals(m2);
        }
    };

    public struct Sphere {
        public Vector3 position;
        public float radius;
        public RayTraceParams lighting;

        public bool Equals(Sphere other) {
            return (other.position == position && other.radius == radius && other.lighting == lighting);
        }

        public override bool Equals(object obj) {
            return (obj != null && GetType() == obj.GetType() && this.Equals((Sphere) obj));
        }

        public override int GetHashCode() {
            unchecked {
                int hash = 17;
                hash = (hash * 23) + position.GetHashCode();
                hash = (hash * 23) + radius.GetHashCode();
                hash = (hash * 23) + lighting.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(Sphere s1, Sphere s2) {
            return s1.Equals(s2);
        }

        public static bool operator !=(Sphere s1, Sphere s2) {
            return !s1.Equals(s2);
        }
    };

    public struct BVHNode {
        public Vector3 vmin;
        public Vector3 vmax;
        public int index;
    };

    // Bounding Volume Trees
    private static List<BVHNode> MeshBVH = new List<BVHNode>();
    private static int MeshDepth = 0;
    
    private static List<BVHNode> SphereBVH = new List<BVHNode>();
    private static int SphereDepth = 0;

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
            _normalBuffer.Release();
        }

        if (_sphereBVHBuffer != null) {
            _sphereBVHBuffer.Release();
        }

        if (_meshObjectBVHBuffer != null) {
            _meshObjectBVHBuffer.Release();
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
        _normals.Clear();
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
                    _lighting.color_albedo = new Vector3(obj.albedoColor.r, obj.albedoColor.g, obj.albedoColor.b);
                    _lighting.color_specular = new Vector3(obj.specularColor.r, obj.specularColor.g, obj.specularColor.b);
                    _lighting.emission = new Vector3(obj.emissionColor.r, obj.emissionColor.g, obj.emissionColor.b);
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
                    _normals.AddRange(ComputeNormals(indices.Select(index => index + firstVertex).ToArray()));

                    // Add Lighting Parameters
                    _lighting.color_albedo = new Vector3(obj.albedoColor.r, obj.albedoColor.g, obj.albedoColor.b);
                    _lighting.color_specular = new Vector3(obj.specularColor.r, obj.specularColor.g, obj.specularColor.b);
                    _lighting.emission = new Vector3(obj.emissionColor.r, obj.emissionColor.g, obj.emissionColor.b);
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

    /// GetIndexList(List<T>)
    /*
    private List<int> GetIndexList(Vector3[] array, Vector3 vec) {
        List<int> indices = new List<int>();

        for (int index = 0; index < array.Length; index++) {
            if (array[index] == vec) {
                indices.Add(index - index % 3);
            }
        }

        return indices;
    }//*/

    /// ComputeNormals(Mesh, Vertex): Compute the normals associated with the vertices of a mesh ///
    private List<Vector3> ComputeNormals(int[] indices) {
        // Variables
        List<Vector3> normals = new List<Vector3>();

        Debug.Log("Indices Length: " + indices.Length);
        Debug.Log("Indices[0]: " + indices[0]);
        Debug.Log("Indices[end]: " + indices[indices.Length - 1]);

        // Calculate normals for every vertex
        for (int i = 0; i < indices.Length; i++) {
            // Setup normal vector
            Vector3 vec = new Vector3(0.0f, 0.0f, 0.0f);

            // Find base indices for triangles that share this vertex
            int[] nearby = indices.Where(index => _vertices[index] == _vertices[_indices[i]]).ToArray();

            // Add normals from all touching triangles
            for (int j = 0; j < nearby.Length; j++) {
                // Correct index to use first vertex of triangle
                int start = nearby[j] - (nearby[j] % 3);

                // Calculate cross product between two edges to get triangle normal
                vec += Vector3.Cross(_vertices[indices[start + 1]] - _vertices[indices[start]], _vertices[indices[start + 2]] - _vertices[indices[start]]);
            }
             
            // Add averaged normal vector to list
            normals.Add(Vector3.Normalize(vec));
        }

        // Return list of normals
        return normals;
    }

    /// ComputeCenter(MeshObjects): Compute Ideal Splitting Center of all mesh objects in bounding box ///
    private Vector3 ComputeCenter(List<MeshObject> meshObjects) {
        // Variables
        Vector3 avePoint = new Vector3();
        Vector3 centerPoint = new Vector3();
        float numPoints = 0.0f;
        int lastIndex;

        // Adding Coordinates
        foreach (MeshObject obj in meshObjects) {
            // Find Mesh Object Center
            lastIndex = obj.indices_count + obj.indices_offset;
            for (int i = obj.indices_offset; i < lastIndex; i++) {
                centerPoint += _vertices[i];
            }

            // Add center to averaging vector
            avePoint += centerPoint / obj.indices_count;
            numPoints++;
        }

        // Return
        return avePoint / numPoints;
    }
    
    /// ComputeCenter(Spheres): Compute center of all spheres in bounding box ///
    private Vector3 ComputeCenter(List<Sphere> spheres) {
        // Variables
        Vector3 avePoint = new Vector3(0.0f, 0.0f, 0.0f);
        float numPoints = (float) spheres.Count;

        // Adding Coordinates
        foreach (Sphere s in spheres) {
            avePoint += s.position;
        }

        // Return
        return avePoint / numPoints;
    }

    /// SplitBounds() ///
    private Bounds[] SplitBounds(Vector3 start, Vector3 extents, LayerMask mask, int count) {
        // Setup Variables
        float[] counts = {count / 2.0f, count / 2.0f, count / 2.0f};
        int hitIndex = 0;

        // Test Collision boxes along each axis
        Collider[][] FrontHits = new Collider[3][];
        Collider[][] BackHits = new Collider[3][];

        FrontHits[0] = Physics.OverlapBox(start + new Vector3(extents.x / 2.0f, 0, 0), new Vector3(extents.x / 2.0f, extents.y, extents.z), Quaternion.identity, mask, QueryTriggerInteraction.Ignore); // x-axis test
        FrontHits[1] = Physics.OverlapBox(start + new Vector3(0, extents.y / 2.0f, 0), new Vector3(extents.x, extents.y / 2.0f, extents.z), Quaternion.identity, mask, QueryTriggerInteraction.Ignore); // y-axis test
        FrontHits[2] = Physics.OverlapBox(start + new Vector3(0, 0, extents.z / 2.0f), new Vector3(extents.x, extents.y, extents.z / 2.0f), Quaternion.identity, mask, QueryTriggerInteraction.Ignore); // z-axis test

        BackHits[0] = Physics.OverlapBox(start - new Vector3(extents.x / 2.0f, 0, 0), new Vector3(extents.x / 2.0f, extents.y, extents.z), Quaternion.identity, mask, QueryTriggerInteraction.Ignore); // x-axis test
        BackHits[1] = Physics.OverlapBox(start - new Vector3(0, extents.y / 2.0f, 0), new Vector3(extents.x, extents.y / 2.0f, extents.z), Quaternion.identity, mask, QueryTriggerInteraction.Ignore); // y-axis test
        BackHits[2] = Physics.OverlapBox(start - new Vector3(0, 0, extents.z / 2.0f), new Vector3(extents.x, extents.y, extents.z / 2.0f), Quaternion.identity, mask, QueryTriggerInteraction.Ignore); // z-axis test

        // Compare slicing axes
        for (int i = 0; i < 3; i++) {
            counts[i] = Mathf.Abs(counts[i] - FrontHits[i].Length);

            if (Mathf.Abs(counts[i]) <= Mathf.Min(counts)) 
                hitIndex = i;
        }

        // Debug Report
        if (DEBUG_LEVEL == 1) {
            int overlapCount = 0;
            for (int i = 0; i < FrontHits[hitIndex].Length; i++) {
                if (BackHits[hitIndex].Contains(FrontHits[hitIndex][i]))
                    overlapCount++;
            }
            
            Debug.Log(" > Total: " + count + ", Slice Axis: " + hitIndex + ", Front hits: " + FrontHits[hitIndex].Length + ", Backhits: " + BackHits[hitIndex].Length + ", Overlaps: " + overlapCount);
        }

        // Create largest extents for child bounds based on winning axis
        Bounds[] final = new Bounds[2];

        if (FrontHits[hitIndex].Length > 0) {
            // Setup first bounds
            Bounds bound1 = FrontHits[hitIndex][0].bounds;

            // Loop through remaining bounds
            foreach (Collider hit in FrontHits[hitIndex]) {
                // Find object and object index
                RayTraceObject obj = hit.gameObject.GetComponent<RayTraceObject>();
                int index = _rayTraceObjects.FindIndex(x => x == obj); 

                // Encapsulate Object
                if (index != -1) {
                    bound1.Encapsulate(obj.bounds);
                }
            }

            // Setup for return
            final[0] = bound1;
        }

        if (BackHits[hitIndex].Length > 0) {
            // Setup first bounds
            Bounds bound2 = BackHits[hitIndex][0].bounds;

            // Loop through remaining bounds
            foreach (Collider hit in BackHits[hitIndex]) {
                // Find object and object index
                RayTraceObject obj = hit.gameObject.GetComponent<RayTraceObject>();
                int index = _rayTraceObjects.FindIndex(x => x == obj); 

                // Encapsulate Object
                if (index != -1) {
                    bound2.Encapsulate(obj.bounds);
                }
            }

            // Setup for return
            final[1] = bound2;
        }

        // Return Child Nodes
        return final;
    }

    /// GetMeshObjectsInBound() /// 
    private List<MeshObject> GetMeshObjectsInBound(Bounds bounds) {
        List<MeshObject> list = new List<MeshObject>();

        foreach (RayTraceObject obj in _rayTraceObjects) {
            if (obj.type == 0 && bounds.Intersects(obj.bounds)) {
                list.Add(_meshObjects[_rayTraceObjectIndices[_rayTraceObjects.FindIndex(x => x == obj)]]);
            }
        }

        return list;
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

    /// CreateBVH(): Create BVH for MeshObjects in scene ///
    private void CreateBVH(Bounds parent, List<MeshObject> meshes, int depth, int index = 0) {
        // Setup Variables
        int numObjects = meshes.Count;

        // Generate Child Bounds & Lists
        // Bounds[] newBounds = SplitBounds(ComputeCenter(meshes), parent.extents, LayerMask.GetMask("RayTrace_mesh"), numObjects); // [WIP] ================= ! ! ! ! !
        Bounds[] newBounds = SplitBounds(parent.center, parent.extents, LayerMask.GetMask("RayTrace_mesh"), numObjects);
        List<MeshObject> list1 = GetMeshObjectsInBound(newBounds[0]);
        List<MeshObject> list2 = GetMeshObjectsInBound(newBounds[1]);

        // Debug Report
        if (DEBUG_LEVEL == 1) {
            Debug.Log("[MESH OBJECTS] SplitBounds_MeshObjects() called on " + numObjects + " object(s)");
            Debug.Log(" > Child1 List Length = " + list1.Count + ", Child2 List Length = " + list2.Count);
        }

        // Decisions if nonempty
        if (numObjects > 0) {
            // Left Child Decisions
            if (numObjects > 1 && depth <= MeshDepth) {
                // Recursion
                if (list1.Count > 0 && depth < MeshDepth) {
                    CreateBVH(newBounds[0], list1, depth + 1, index*2 + 1);
                } else if (list1.Count > 1) {
                    // DEBUG Message for now, will figure out how to handle better later !!!!!
                    if (DEBUG_LEVEL >= 1) {
                        Debug.Log("<WARNING> Mesh object(s) left out of tree!");
                    }
                }
            }

            // Right Child Decisions
            if (numObjects > 1 && depth <= MeshDepth) {
                // Recursion
                if (list2.Count > 0 && depth < MeshDepth) {
                    CreateBVH(newBounds[1], list2, depth + 1, index*2 + 2);
                } else if (list2.Count > 1) {
                    // DEBUG Message for now, will figure out how to handle better later !!!!!
                    if (DEBUG_LEVEL >= 1) {
                        Debug.Log("<WARNING>  Mesh object(s) left out of tree!");
                    }
                }
            }

            // Node Self Addition
            BVHNode node = new BVHNode();
            node.vmin = parent.min;
            node.vmax = parent.max;
            node.index = -1;

            if (numObjects == 1 || (numObjects >= 1 && depth >= MeshDepth)) {
                node.index = _meshObjects.FindIndex(x => (x == meshes[0]));
            }

            MeshBVH.RemoveAt(index);
            MeshBVH.Insert(index, node);
        }
    }

    /// CreateBVH(): Create BVH for Spheres in scene ///
    private void CreateBVH(Bounds parent, List<Sphere> spheres, int depth, int index = 0) {
        // Setup Variables
        int numObjects = spheres.Count;

        // Generate Child Bounds & Lists
        Bounds[] newBounds = SplitBounds(ComputeCenter(spheres), parent.extents, LayerMask.GetMask("RayTrace_sphere"), numObjects);
        List<Sphere> list1 = GetSpheresInBound(newBounds[0]);
        List<Sphere> list2 = GetSpheresInBound(newBounds[1]);

        // Debug Report
        if (DEBUG_LEVEL == 1) {
            Debug.Log("[SPHERES] SplitBounds_Spheres() called on " + numObjects + " object(s)");
            Debug.Log(" > Child1 List Length = " + list1.Count + ", Child2 List Length = " + list2.Count);
        }

        // Decisions if nonempty
        if (numObjects > 0) {
            // Left Child Decisions
            if (numObjects > 1 && depth <= SphereDepth) {
                // Recursion
                if (list1.Count > 0 && depth < SphereDepth) {
                    CreateBVH(newBounds[0], list1, depth + 1, index*2 + 1);
                } else if (list1.Count > 1) {
                    // DEBUG Message for now, will figure out how to handle better later !!!!!
                    if (DEBUG_LEVEL >= 1) {
                        Debug.Log("<WARNING> Sphere(s) left out of tree!");
                    }
                }
            }

            // Right Child Decisions
            if (numObjects > 1 && depth <= SphereDepth) {
                // Recursion
                if (list2.Count > 0 && depth < SphereDepth) {
                    CreateBVH(newBounds[1], list2, depth + 1, index*2 + 2);
                } else if (list2.Count > 1) {
                    // DEBUG Message for now, will figure out how to handle better later !!!!!
                    if (DEBUG_LEVEL >= 1) {
                        Debug.Log("<WARNING> Sphere(s) left out of tree!");
                    }
                }
            }

            // Node Self Addition
            BVHNode node = new BVHNode();
            node.vmin = parent.min;
            node.vmax = parent.max;
            node.index = -1;

            if (numObjects == 1 || (numObjects >= 1 && depth >= SphereDepth)) {
                node.index = _spheres.FindIndex(x => (x == spheres[0]));
            }

            SphereBVH.RemoveAt(index);
            SphereBVH.Insert(index, node);
        }
    }

    /// PrepareBVHList(): Prepare BVH list to be used with empty nodes ///
    private void PrepareBVHList(List<BVHNode> list, int length) {
        for (int i = 0; i < length; i++) {
            list.Add(new BVHNode() { 
                vmin = new Vector3(0.0f, 0.0f, 0.0f),
                vmax = new Vector3(0.0f, 0.0f, 0.0f),
                index = -1
            });
        }
    }

    /// TrimBVHList(): Trim empty nodes off the end of the list ///
    private void TrimBVHList(List<BVHNode> list) {
        int i = list.Count - 1;

        while(i > 0 && list[i].index < 0) {
            list.RemoveAt(i);
            i--;
        }
    }

    /// RebuildTrees(): Rebuild Bounding Volume Tree for Ray Trace Objects ///
    private void RebuildTrees(Bounds[] rootBounds) {
        // Mesh BVH Tree Rebuilding
        MeshDepth = Mathf.CeilToInt(Mathf.Log(_meshObjects.Count)) + 1;
        int MeshLength = (int) Mathf.Round(Mathf.Pow(2.0f, (float) MeshDepth)) - 1;
        PrepareBVHList(MeshBVH, MeshLength);
        CreateBVH(rootBounds[0], _meshObjects, 1);
        TrimBVHList(MeshBVH);

        // Sphere BVH Tree Rebuilding
        SphereDepth = Mathf.CeilToInt(Mathf.Log(_spheres.Count)) + 1;
        int SphereLength = (int) Mathf.Round(Mathf.Pow(2.0f, (float) SphereDepth)) - 1;
        PrepareBVHList(SphereBVH, SphereLength);
        CreateBVH(rootBounds[1], _spheres, 1);
        TrimBVHList(SphereBVH);

        // Debug Reporting
        if (DEBUG_LEVEL == 1 || DEBUG_LEVEL == 2) {
            Debug.Log("[MESH OBJECTS] Depth: " + MeshDepth + ", Expected Length: " + MeshLength + ", Real Length: " + MeshBVH.Count);
            Debug.Log("[SPHERES] Depth: " + SphereDepth + ", Expected Length: " + SphereLength + ", Real Length: " + SphereBVH.Count);
        }

        // Update Computer buffers
        CreateComputeBuffer(ref _meshObjectBuffer, _meshObjects, MeshObjectStructSize);
        CreateComputeBuffer(ref _vertexBuffer, _vertices, 12);
        CreateComputeBuffer(ref _indexBuffer, _indices, 4);
        CreateComputeBuffer(ref _normalBuffer, _normals, 12);
        CreateComputeBuffer(ref _sphereBuffer, _spheres, SphereStructSize);

        CreateComputeBuffer(ref _meshObjectBVHBuffer, MeshBVH, BVHNodeSize);
        CreateComputeBuffer(ref _sphereBVHBuffer, SphereBVH, BVHNodeSize);
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
        RayTraceShader.SetVector("_PixelOffset", new Vector2(Random.value, Random.value));
        RayTraceShader.SetFloat("_Seed", Random.value);

        RayTraceShader.SetInt("_numBounces", numBounces);
        RayTraceShader.SetInt("_numRays", numRays);

        RayTraceShader.SetInt("_MeshBVH_len", MeshBVH.Count);
        RayTraceShader.SetInt("_SphereBVH_len", SphereBVH.Count);


        SetComputeBuffer("_MeshObjects", _meshObjectBuffer);
        SetComputeBuffer("_Vertices", _vertexBuffer);
        SetComputeBuffer("_Indices", _indexBuffer);
        SetComputeBuffer("_Normals", _normalBuffer);
        SetComputeBuffer("_Spheres", _sphereBuffer);

        SetComputeBuffer("_MeshBVH", _meshObjectBVHBuffer);
        SetComputeBuffer("_SphereBVH", _sphereBVHBuffer);
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

    /// GizmosDrawBox(): Debug Box Drawing ///
    private void GizmosDrawBox(Vector3 min, Vector3 max) {
        // Setup Corners
        // NOTE: Corner7 and Corner8 are just max and min directly
        Vector3 corner1 = new Vector3(max.x, min.y, min.z);
        Vector3 corner2 = new Vector3(min.x, max.y, min.z);
        Vector3 corner3 = new Vector3(min.x, min.y, max.z);

        Vector3 corner4 = new Vector3(min.x, max.y, max.z);
        Vector3 corner5 = new Vector3(max.x, min.y, max.z);
        Vector3 corner6 = new Vector3(max.x, max.y, min.z);

        // Drawing lines
        Gizmos.DrawLine(min, corner1);
        Gizmos.DrawLine(min, corner2);
        Gizmos.DrawLine(min, corner3);

        Gizmos.DrawLine(max, corner4);
        Gizmos.DrawLine(max, corner5);
        Gizmos.DrawLine(max, corner6);

        Gizmos.DrawLine(corner1, corner5);
        Gizmos.DrawLine(corner1, corner6);

        Gizmos.DrawLine(corner2, corner4);
        Gizmos.DrawLine(corner2, corner6);

        Gizmos.DrawLine(corner3, corner4);
        Gizmos.DrawLine(corner3, corner5);
    }

    /// GizmosDrawTree(): Debug Tree Drawing ///
    private void GizmosDrawTree(List<BVHNode> list, int depth, Color col, float colChange, int index = 0) {
        if (depth > 0) {
            // Get node
            BVHNode node = list[index];

            // Draw Bounding Volume
            Gizmos.color = col;
            GizmosDrawBox(node.vmin, node.vmax);

            // Draw info (position in tree list, index of object)
            GUIStyle style = new GUIStyle();
            style.normal.textColor = (col*2.0f + Color.white) / 3.0f;
            UnityEditor.Handles.Label((node.vmin + node.vmax) / 2.0f, "(" + index.ToString() + ", " + node.index.ToString() + ")", style);

            // Recursion downwards
            int step = (int) Mathf.Floor(Mathf.Pow(2, depth - 2));
            GizmosDrawTree(list, depth - 1, new Color(col.r + colChange, col.g - colChange, col.b - colChange), colChange, index*2 + 1);
            GizmosDrawTree(list, depth - 1, new Color(col.r + colChange, col.g - colChange, col.b - colChange), colChange, index*2 + 2);
        }
    }

    /// OnDrawGizmos(): Debug Drawing ///
    void OnDrawGizmos() {
        // Drawing BVH Trees
        GizmosDrawTree(SphereBVH, SphereDepth, new Color(0.0f, 0.0f, 1.0f), 1.0f / SphereDepth); // Draw Sphere BVH
        GizmosDrawTree(MeshBVH, MeshDepth, new Color(0.0f, 1.0f, 0.0f), 1.0f / MeshDepth); // Draw Mesh BVH

        // Draw Calculated Normals
        foreach (MeshObject mesh in _meshObjects) {
            for (int i = mesh.indices_offset; i < mesh.indices_offset + mesh.indices_count; i++) {
                Gizmos.color = Color.white;
                Gizmos.DrawSphere(mesh.localToWorldMatrix.MultiplyPoint3x4(_vertices[_indices[i]]), 0.005f);
                Gizmos.color = Color.blue;
                Gizmos.DrawLine(mesh.localToWorldMatrix.MultiplyPoint3x4(_vertices[_indices[i]]), mesh.localToWorldMatrix.MultiplyPoint3x4(_vertices[_indices[i]] + _normals[_indices[i]] * 0.1f));
            }
        }
    }
}