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
    private const float EPSILON = float.Epsilon * 3;
    private RayTraceDebug rayDebug;

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

        public bool Equals(BVHNode other) {
            return (other.vmax == vmax && other.vmin == vmin && other.index == index);
        }

        public override bool Equals(object obj) {
            return (obj != null && GetType() == obj.GetType() && this.Equals((BVHNode) obj));
        }

        public override int GetHashCode() {
            unchecked {
                int hash = 17;
                hash = (hash * 23) + vmin.GetHashCode();
                hash = (hash * 23) + vmax.GetHashCode();
                hash = (hash * 23) + index.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(BVHNode b1, BVHNode b2) {
            return b1.Equals(b2);
        }

        public static bool operator !=(BVHNode b1, BVHNode b2) {
            return !b1.Equals(b2);
        }
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

        // Calculate all normal vectors
        _normals.AddRange(ComputeNormals().ToArray());

        // Debug Report
        rayDebug.Log("# of Spheres: " + _spheres.Count, 2);
        rayDebug.Log("# of Mesh Objects: " + _meshObjects.Count, 2);
        rayDebug.Log("# of Vertices: " + _vertices.Count, 2);
        rayDebug.Log("# of Indices: " + _indices.Count, 2);
        rayDebug.Log("# of Normals: " + _normals.Count, 2);

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

    /// ComputeNormals(Mesh, Vertex): Compute the normals associated with the vertices of a mesh ///
    /* Normals s.t. |Normals| = |Vertices| //*/
    private List<Vector3> ComputeNormals() {
        // Variables
        List<Vector3> normals = new List<Vector3>();

        // Calculate normals for every vertex
        for (int i = 0; i < _vertices.Count; i++) {
            // Setup normal vector
            Vector3 vec = new Vector3(0.0f, 0.0f, 0.0f);

            // Find base indices for triangles that share this vertex
            var query = _indices.Select((int vecIndex, int listIndex) => new {vec = vecIndex, ind = listIndex});
            query = query.Where(indexPair => (_vertices[indexPair.vec] - _vertices[i]).sqrMagnitude <= EPSILON);

            // Add normals from all touching triangles
            foreach (var obj in query) {
                // Correct to first vector in triangle set
                int start = obj.ind - (obj.ind % 3);

                // Calculate cross product between two edges to get triangle normal
                vec += Vector3.Cross(_vertices[_indices[start + 1]] - _vertices[_indices[start]], _vertices[_indices[start + 2]] - _vertices[_indices[start]]);
            }
             
            // Add averaged normal vector to list
            normals.Add(Vector3.Normalize(vec));
        }

        // Return list of normals
        return normals;
    }//*/

    /// ComputeNormals(Mesh, Vertex): Compute the normals associated with the indices of a mesh ///
    /* Normals s.t. |Normals| = |Indices| //*
    private List<Vector3> ComputeNormals() {
        // Variables
        List<Vector3> normals = new List<Vector3>();

        // Calculate normals for every vertex
        for (int i = 0; i < _indices.Count; i++) {
            // Setup normal vector
            Vector3 vec = new Vector3(0.0f, 0.0f, 0.0f);

            // Find base indices for triangles that share this vertex
            var query = _indices.Select((int vecIndex, int listIndex) => new {vec = vecIndex, ind = listIndex});
            query = query.Where(indexPair => (_vertices[indexPair.vec] - _vertices[i]).sqrMagnitude <= EPSILON);

            // Add normals from all touching triangles
            foreach (var obj in query) {
                // Correct to first vector in triangle set
                int start = obj.ind - (obj.ind % 3);

                // Calculate cross product between two edges to get triangle normal
                vec += Vector3.Cross(_vertices[_indices[start + 1]] - _vertices[_indices[start]], _vertices[_indices[start + 2]] - _vertices[_indices[start]]);
            }
             
            // Add averaged normal vector to list
            normals.Add(Vector3.Normalize(vec));
        }

        // Return list of normals
        return normals;
    }//*/

////////////////// Top-to-Bottom BVH Building /////////////////////

    /// ComputeCenter(MeshObjects): Compute ideal splitting center of all mesh objects in bounding box ///
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
    
    /// ComputeCenter(Spheres): Compute ideal splitting center of all spheres in bounding box ///
    private Vector3 ComputeCenter(List<Sphere> spheres) {
        // Variables
        Vector3 avePoint = new Vector3(0.0f, 0.0f, 0.0f);
        Vector3 boundVec = (1.0f / Mathf.Sqrt(2.0f)) * (new Vector3(1.0f, 1.0f, 1.0f));
        float numPoints = (float) spheres.Count;// * 2.0f;

        // Adding Coordinates
        //*
        foreach (Sphere s in spheres) {
            avePoint += s.position;
        }//*/
        /*
        foreach (Sphere s in spheres) {
            avePoint += s.position + s.radius * boundVec; // Maximum bounds
            avePoint += s.position - s.radius * boundVec; // Minimum bounds
        }//*/

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
        if (rayDebug.debugLevel >= 3) {
            int overlapCount = 0;
            for (int i = 0; i < FrontHits[hitIndex].Length; i++) {
                if (BackHits[hitIndex].Contains(FrontHits[hitIndex][i]))
                    overlapCount++;
            }
            
            rayDebug.Log(" > Total: " + count + ", Slice Axis: " + hitIndex + ", Front hits: " + FrontHits[hitIndex].Length + ", Backhits: " + BackHits[hitIndex].Length + ", Overlaps: " + overlapCount, 3);
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

        // Debug Report
        rayDebug.Log("[MESH OBJECTS] SplitBounds_MeshObjects() called on " + numObjects + " object(s)", 3);

        // Generate Child Bounds & Lists
        // Bounds[] newBounds = SplitBounds(ComputeCenter(meshes), parent.extents, LayerMask.GetMask("RayTrace_mesh"), numObjects); // [WIP] ================= ! ! ! ! !
        Bounds[] newBounds = SplitBounds(parent.center, parent.extents, LayerMask.GetMask("RayTrace_mesh"), numObjects);
        List<MeshObject> list1 = GetMeshObjectsInBound(newBounds[0]);
        List<MeshObject> list2 = GetMeshObjectsInBound(newBounds[1]);

        // Debug Report
        rayDebug.Log(" > Child1 List Length = " + list1.Count + ", Child2 List Length = " + list2.Count, 3);

        // Decisions if nonempty
        if (numObjects > 0) {
            // Left Child Decisions
            if (numObjects > 1 && depth <= MeshDepth) {
                // Recursion
                if (list1.Count > 0 && depth < MeshDepth) {
                    CreateBVH(newBounds[0], list1, depth + 1, index*2 + 1);
                } else if (list1.Count > 1) {
                    // DEBUG Message for now, will figure out how to handle better later !!!!!
                    rayDebug.Log("<WARNING> Mesh object(s) left out of tree!", 1);
                }
            }

            // Right Child Decisions
            if (numObjects > 1 && depth <= MeshDepth) {
                // Recursion
                if (list2.Count > 0 && depth < MeshDepth) {
                    CreateBVH(newBounds[1], list2, depth + 1, index*2 + 2);
                } else if (list2.Count > 1) {
                    // DEBUG Message for now, will figure out how to handle better later !!!!!
                    rayDebug.Log("<WARNING>  Mesh object(s) left out of tree!", 1);
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

        // Debug Report
        rayDebug.Log("[SPHERES] SplitBounds_Spheres() called on " + numObjects + " object(s)", 3);

        // Generate Child Bounds & Lists
        Bounds[] newBounds = SplitBounds(ComputeCenter(spheres), parent.extents, LayerMask.GetMask("RayTrace_sphere"), numObjects);
        List<Sphere> list1 = GetSpheresInBound(newBounds[0]);
        List<Sphere> list2 = GetSpheresInBound(newBounds[1]);

        // Debug Report
        rayDebug.Log(" > Child1 List Length = " + list1.Count + ", Child2 List Length = " + list2.Count, 3);

        // Decisions if nonempty
        if (numObjects > 0) {
            // Left Child Decisions
            if (numObjects > 1 && depth <= SphereDepth) {
                // Recursion
                if (list1.Count > 0 && depth < SphereDepth) {
                    CreateBVH(newBounds[0], list1, depth + 1, index*2 + 1);
                } else if (list1.Count > 1) {
                    // DEBUG Message for now, will figure out how to handle better later !!!!!
                    rayDebug.Log("<WARNING> Sphere(s) left out of tree!", 1);
                }
            }

            // Right Child Decisions
            if (numObjects > 1 && depth <= SphereDepth) {
                // Recursion
                if (list2.Count > 0 && depth < SphereDepth) {
                    CreateBVH(newBounds[1], list2, depth + 1, index*2 + 2);
                } else if (list2.Count > 1) {
                    // DEBUG Message for now, will figure out how to handle better later !!!!!
                    rayDebug.Log("<WARNING> Sphere(s) left out of tree!", 1);
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
        /// TESTING /// =====================================================================!!!!
        SetupBVHRankList(SetupBVHLeaves(_spheres));

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
        rayDebug.Log("[MESH OBJECTS] Depth: " + MeshDepth + ", Expected Length: " + MeshLength + ", Real Length: " + MeshBVH.Count, 2);
        rayDebug.Log("[SPHERES] Depth: " + SphereDepth + ", Expected Length: " + SphereLength + ", Real Length: " + SphereBVH.Count, 2);

        // Update Computer buffers
        CreateComputeBuffer(ref _meshObjectBuffer, _meshObjects, MeshObjectStructSize);
        CreateComputeBuffer(ref _vertexBuffer, _vertices, 12);
        CreateComputeBuffer(ref _indexBuffer, _indices, 4);
        CreateComputeBuffer(ref _normalBuffer, _normals, 12);
        CreateComputeBuffer(ref _sphereBuffer, _spheres, SphereStructSize);

        CreateComputeBuffer(ref _meshObjectBVHBuffer, MeshBVH, BVHNodeSize);
        CreateComputeBuffer(ref _sphereBVHBuffer, SphereBVH, BVHNodeSize);
    }

//////////////////============================/////////////////////

////////////////// Bottom-to-Top BVH Building /////////////////////

    /// SetupBVHLeaves(): Setup Leaf Nodes at bottom of Mesh BVH tree ///
    /*
    private List<BVHNode> SetupBVHLeaves(List<MeshObject> meshes) {
        //
        // WIP
    } //*/

    /// SetupBVHLeaves(): Setup Leaf Nodes at bottom of Sphere BVH tree ///
    private List<BVHNode> SetupBVHLeaves(List<Sphere> spheres) {
        // Setup Variables
        List<BVHNode> leaves = new List<BVHNode>();
        
        // Add a tight bound BVHNode for each sphere 
        foreach (Sphere sphere in spheres) {
            leaves.Add(new BVHNode() {
                vmin = sphere.position - new Vector3(-sphere.radius, -sphere.radius, -sphere.radius),
                vmax = sphere.position - new Vector3(sphere.radius, sphere.radius, sphere.radius),
                index = _spheres.FindIndex(x => x == sphere)
            });
        }

        // Return
        return leaves;
    }

    /// SetupBVHPairingLists(): Setup node ranking list for each node ///
    /// Ranking List: All other nodes sorted nearest-to-farthest in 2 concatenated lists: first regular nodes then, "forbidden" nodes
    ///  - "Forbidden" node : A node where the vector between it and the chosen node intersects at least one other node. Disfavored to reduce overlap for node pairings
    private List<List<BVHNode>> SetupBVHRankList(List<BVHNode> nodes) {
        // Setup Variables
        List<List<BVHNode>> rankList = new List<List<BVHNode>>();
        List<(int index, double dis)> ranking = new List<(int index, double dis)>();
        List<BVHNode> curList;
        
        double distance;
        Vector3 test = new Vector3();
        float lineDis = 0.0f;

        // Build rank list for each node
        foreach (BVHNode chosen in nodes) {
            // Reset Variables
            curList = new List<BVHNode>();
            ranking.Clear();

            // Finding Distance and forbiddenness
            foreach (BVHNode other in nodes) {
                if (chosen != other) {
                    // Calculate distance
                    distance = Mathf.Min(Vector3.SqrMagnitude(chosen.vmax - other.vmin), Vector3.SqrMagnitude(chosen.vmin - other.vmax));

                    // Decide if Forbidden (mark with negative distance value)
                    lineDis = 0.0f;
                    test = (chosen.vmax + chosen.vmin) / 2.0f - (other.vmax + other.vmin) / 2.0f;

                    if (Vector3.Magnitude(test) != 0) {
                        foreach (BVHNode bystander in nodes) {
                            if (bystander != chosen && bystander != other) {
                                // Find perpendicular distance of bystander center to test vector
                                lineDis = Vector3.Magnitude(Vector3.Cross((bystander.vmax + bystander.vmin) / 2.0f, test)) / Vector3.Magnitude(test);

                                // Decide it intersects if closer than diagonal distance of bystander
                                if (lineDis <= Vector3.Magnitude(bystander.vmax - bystander.vmin) / 2.0f) {
                                    distance *= -1;
                                }
                            }
                        }
                    }

                    // Add to ranking
                    ranking.Add( (nodes.FindIndex(x => x == other), distance) );
                }
            }

            // Sort Ranking
            ranking.Sort(delegate((int index, double dis) x, (int index, double dis) y) {
                // Mark equal distances equal
                if (x.dis == y.dis) return 0;

                // Sort negative distances (AKA forbidden nodes) to the left of positive distances (treat as negative being greater)
                if (Mathf.Sign((float) x.dis) != Mathf.Sign((float) y.dis)) {
                    if (x.dis < y.dis) {
                        return 1;
                    } else { 
                        return -1;
                    }
                }

                // Sort nearest to farthest of absolute value for same sign
                if (x.dis < 0 || y.dis < 0) {
                    return (-x.dis).CompareTo(-y.dis);
                }

                return (x.dis).CompareTo(y.dis);
            });

            // Copy sorted order to BVHNode list using indices
            foreach ((int index, double dis) pair in ranking) {
                curList.Add(nodes[pair.index]);
            }

            // Store Current BVHList
            rankList.Add(curList);
        }

        // Return
        return rankList;
    }

    /// PairBVHBounds(): Setup Leaf Nodes at bottom of BVH tree ///
    /*    
    private List<Bounds> PairBVHBounds(List<Bounds> nodes, ref float volume_general, ref float volume_overlap) {
        // Setup Variables
        List<Bounds> pairings = new List<Bounds>();
        
        //
    } //*/

//////////////////============================/////////////////////

    /// Awake(): Setup ///
    private void Awake() {
        // Getting Camera
        _camera = GetComponent<Camera>();

        // Getting Debug Component
        rayDebug = GetComponent<RayTraceDebug>();
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

    /// OnDrawGizmos(): Debug Drawing ///
    void OnDrawGizmos() {
        if (rayDebug != null) {
            // Drawing BVH Trees
            rayDebug.DrawBVHTree(MeshBVH, MeshDepth, 0);
            rayDebug.DrawBVHTree(SphereBVH, SphereDepth, 1);

            // Draw Calculated Normals
            rayDebug.DrawNormals(_meshObjects, _vertices, _indices, _normals);
        }
    }
}