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
    private void RebuildObjectLists() {
        //  & Clearing Lists
        _rayTraceObjectIndices.Clear();
        _meshObjects.Clear();
        _vertices.Clear();
        _indices.Clear();
        _normals.Clear();
        _spheres.Clear();

        // Loop over all Ray Trace Objects and gather their data
        foreach (RayTraceObject obj in _rayTraceObjects) {
            // Setup Variables
            RayTraceParams _lighting = new RayTraceParams();

            // Type Specific Setup
            switch(obj.type) {
                case 1: // Handling Spheres
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

////////////////// Bottom-to-Top BVH Building /////////////////////

    /// SetupBVHLeaves(): Setup Leaf Nodes at bottom of Mesh BVH tree ///
    private List<BVHNode> SetupBVHLeaves(List<MeshObject> meshes) {
        // Setup Variables
        List<BVHNode> leaves = new List<BVHNode>();
        Vector3 test;
        BVHNode leaf;
        
        // Add a tight bound BVHNode for each meshObject
        foreach (MeshObject mesh in meshes) {
            // Create node
            leaf = new BVHNode() {
                vmin = mesh.localToWorldMatrix.MultiplyPoint3x4(_vertices[_indices[0]]),
                vmax = mesh.localToWorldMatrix.MultiplyPoint3x4(_vertices[_indices[0]]),
                index = _meshObjects.FindIndex(x => x == mesh)
            };

            // Refine bounds
            for (int i = mesh.indices_offset + 1; i < mesh.indices_offset + mesh.indices_count; i++) {
                test = mesh.localToWorldMatrix.MultiplyPoint3x4(_vertices[_indices[i]]);
                leaf.vmin = new Vector3(Mathf.Min(leaf.vmin.x, test.x), Mathf.Min(leaf.vmin.y, test.y), Mathf.Min(leaf.vmin.z, test.z));
                leaf.vmax = new Vector3(Mathf.Max(leaf.vmax.x, test.x), Mathf.Max(leaf.vmax.y, test.y), Mathf.Max(leaf.vmax.z, test.z));
            }

            // Add leaf
            leaves.Add(leaf);
        }

        // Return
        return leaves;
    }

    /// SetupBVHLeaves(): Setup Leaf Nodes at bottom of Sphere BVH tree ///
    private List<BVHNode> SetupBVHLeaves(List<Sphere> spheres) {
        // Setup Variables
        List<BVHNode> leaves = new List<BVHNode>();
        BVHNode leaf;
        
        // Add a tight bound BVHNode for each sphere 
        foreach (Sphere sphere in spheres) {
            // Create node
            leaf = new BVHNode() {
                vmin = sphere.position - new Vector3(-sphere.radius, -sphere.radius, -sphere.radius),
                vmax = sphere.position - new Vector3(sphere.radius, sphere.radius, sphere.radius),
                index = _spheres.FindIndex(x => x == sphere)
            };

            // Add leaf
            leaves.Add(leaf);
        }

        // Return
        return leaves;
    }

    /// JoinBVH(): Join two BVH lists to obey index rule for children ///
    private List<BVHNode> JoinBVH(BVHNode parent, List<BVHNode> left, List<BVHNode> right) {
        // Setup Variables
        List<BVHNode> list;
        int depth = Mathf.CeilToInt(Mathf.Max(Mathf.Log(left.Count, 2), Mathf.Log(right.Count, 2))) + 1;
        if (depth <= 1) depth = 2; // Fix case where Log(1) = 0 makes depth = 1 instead of 2
        int startIndex = 0;
        int subLen = 1;

        // Swap children if left sub tree smaller
        if (right.Count > left.Count) {
            list = left;
            left = right;
            right = list;
        }

        // Setup larger list
        list = new List<BVHNode>();
        list.Add(parent);

        // Weave in children layer by layer
        for (int i = 1; i < depth; i++) {
            // Add left subtree layer (guaranteed to be complete)
            for (int k = 0; k < subLen; k++) {
                list.Add(left[startIndex + k]);
            }

            // Add right subtree layer (might be incomplete, so needs filler nodes)
            for (int k = 0; k < subLen; k++) {
                if (right.Count > startIndex + k) {
                    list.Add(right[startIndex + k]);
                } else {
                    list.Add(new BVHNode() {
                        vmin = new Vector3(0.0f, 0.0f, 0.0f),
                        vmax = new Vector3(0.0f, 0.0f, 0.0f),
                        index = -1
                    });
                }
            }

            // Increment for next layer
            startIndex += subLen;
            subLen *= 2;
        }

        // Return
        return list;
    }

    /// SetupBVHRankList(): Setup node ranking list for each node ///
    /// Ranking List: All other nodes sorted nearest-to-farthest in 2 concatenated lists: first regular nodes then, "forbidden" nodes
    ///  - "Forbidden" node : A node where the vector between it and the chosen node roughly intersects at least one other node. (Disfavored to reduce overlap for node pairings)
    private List<List<BVHNode>> SetupBVHRankList(List<List<BVHNode>> trees) {
        // Setup Variables
        List<List<BVHNode>> rankList = new List<List<BVHNode>>();
        List<(int index, double dis)> ranking = new List<(int index, double dis)>();
        List<BVHNode> curList;

        List<BVHNode> nodes = new List<BVHNode>();
        foreach (List<BVHNode> tree in trees) {
            nodes.Add(tree[0]);
        }
        
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

    /// PairBVHBounds(): Choose ideal pairing of current subtrees to make new layer of subtrees ///  
    private void PairBVHBounds(ref List<List<BVHNode>> trees) {
        // Setup Variables
        List<List<BVHNode>> rankList = SetupBVHRankList(trees);

        List<List<BVHNode>> bestPairing = new List<List<BVHNode>>();
        float bestVolume = -1.0f;

        List<List<BVHNode>> pairing = new List<List<BVHNode>>();
        float volume;
        List<bool> paired = new List<bool>();

        List<BVHNode> nodes = new List<BVHNode>();
        foreach (List<BVHNode> tree in trees) {
            nodes.Add(tree[0]);
        }

        int numTests = nodes.Count;
        int index;
        int otherIndex;

        BVHNode node;
        
        // Test different pairings
        for (int i = 0; i < numTests; i++) {
            // Setup Variables
            pairing.Clear();
            paired.Clear();
            for (int n = 0; n < nodes.Count; n++) {paired.Add(false);}
            volume = 0.0f;

            // Make Candidate pairing
            for (int j = i; j < i + numTests; j++) {
                // Index for Choosing node
                index = j % trees.Count;

                if (paired[index] == false) {
                    // Choose first other node in rankList that is unpaired
                    for (int k = 0; k < rankList[index].Count; k++) {
                        otherIndex = nodes.FindIndex(x => x == rankList[index][k]);
                        if (!paired[otherIndex]) {
                            // Confirm Pairing
                            paired[index] = true;
                            paired[otherIndex] = true;

                            // Create parent
                            node = new BVHNode() {
                                vmin = new Vector3(Mathf.Min(Mathf.Min(nodes[index].vmin.x, nodes[otherIndex].vmin.x), Mathf.Min(nodes[index].vmax.x, nodes[otherIndex].vmax.x)),
                                    Mathf.Min(Mathf.Min(nodes[index].vmin.y, nodes[otherIndex].vmin.y), Mathf.Min(nodes[index].vmax.y, nodes[otherIndex].vmax.y)),
                                    Mathf.Min(Mathf.Min(nodes[index].vmin.z, nodes[otherIndex].vmin.z), Mathf.Min(nodes[index].vmax.z, nodes[otherIndex].vmax.z))),
                                vmax = new Vector3(Mathf.Max(Mathf.Max(nodes[index].vmin.x, nodes[otherIndex].vmin.x), Mathf.Max(nodes[index].vmax.x, nodes[otherIndex].vmax.x)),
                                    Mathf.Max(Mathf.Max(nodes[index].vmin.y, nodes[otherIndex].vmin.y), Mathf.Max(nodes[index].vmax.y, nodes[otherIndex].vmax.y)),
                                    Mathf.Max(Mathf.Max(nodes[index].vmin.z, nodes[otherIndex].vmin.z), Mathf.Max(nodes[index].vmax.z, nodes[otherIndex].vmax.z))),
                                index = -1
                            };

                            // Add to pairing, children and volume
                            pairing.Add(JoinBVH(node, trees[index], trees[otherIndex]));
                            volume += (node.vmax.x - node.vmin.x) * (node.vmax.y - node.vmin.y) * (node.vmax.z - node.vmin.z);

                            // Exit for loop
                            k = nodes.Count;
                        }
                    }

                    // Pair lone node as self
                    if (!paired[index]) {
                        node = nodes[index];
                        pairing.Add(JoinBVH(node, trees[index], new List<BVHNode>()));
                        volume += (node.vmax.x - node.vmin.x) * (node.vmax.y - node.vmin.y) * (node.vmax.z - node.vmin.z);
                    }
                }

                // Choose if Total node volumes lowest found so far
                if (volume < bestVolume || bestVolume < 0.0f) {
                    bestPairing = pairing;
                    bestVolume = volume;
                }
            }
        }

        // Replace trees
        trees = bestPairing;
    }

    /// CreateBVH(): Create MeshObject BVH Tree ///
    private void CreateBVH(List<MeshObject> meshes) {
        // Set up BVH list representation
        MeshDepth = Mathf.CeilToInt(Mathf.Log(meshes.Count, 2)) + 1;

        // Setup leaf layer
        List<BVHNode> leaves = SetupBVHLeaves(meshes);
        List<List<BVHNode>> trees = new List<List<BVHNode>>();
        for (int i = 0; i < leaves.Count; i++) {
            trees.Add(new List<BVHNode>());
            trees[i].Add(leaves[i]);
        }

        // Make pairings until at root BVHNode
        for (int i = 0; i < MeshDepth - 1; i++) {
            PairBVHBounds(ref trees);
        }
        
        // Set as official Mesh BVH
        MeshBVH = trees[0];
    }

    /// CreateBVH(): Create Sphere BVH Tree ///
    private void CreateBVH(List<Sphere> spheres) {
        // Set up BVH list representation
        SphereDepth = Mathf.CeilToInt(Mathf.Log(spheres.Count, 2)) + 1;

        // Setup leaf layer
        List<BVHNode> leaves = SetupBVHLeaves(spheres);
        List<List<BVHNode>> trees = new List<List<BVHNode>>();
        for (int i = 0; i < leaves.Count; i++) {
            trees.Add(new List<BVHNode>());
            trees[i].Add(leaves[i]);
        }

        // Make pairings until at root BVHNode
        for (int i = 0; i < SphereDepth - 1; i++) {
            PairBVHBounds(ref trees);
        }
        
        // Set as official Sphere BVH
        SphereBVH = trees[0];
    }

    /// RebuildTrees(): Rebuild Bounding Volume Tree for Ray Trace Objects ///
    private void RebuildTrees() {
        // BVH Tree Rebuilding
        CreateBVH(_meshObjects);
        CreateBVH(_spheres);

        // Debug Reporting
        int MeshLength = (int) Mathf.Round(Mathf.Pow(2.0f, (float) MeshDepth)) - 1;
        int SphereLength = (int) Mathf.Round(Mathf.Pow(2.0f, (float) SphereDepth)) - 1;

        rayDebug.Log("[MESH OBJECTS] \n > Amount: " + _meshObjects.Count + "\n > Depth: " + MeshDepth + "\n > Complete Length: " + MeshLength + "\n > Real Length: " + MeshBVH.Count, 2);
        rayDebug.Log("[SPHERES] \n > Amount: " + _spheres.Count + "\n > Depth: " + SphereDepth + "\n > Complete Length: " + SphereLength + "\n > Real Length: " + SphereBVH.Count, 2);

        // Update Compute buffers
        CreateComputeBuffer(ref _meshObjectBuffer, _meshObjects, MeshObjectStructSize);
        CreateComputeBuffer(ref _vertexBuffer, _vertices, 12);
        CreateComputeBuffer(ref _indexBuffer, _indices, 4);
        CreateComputeBuffer(ref _normalBuffer, _normals, 12);
        CreateComputeBuffer(ref _sphereBuffer, _spheres, SphereStructSize);

        CreateComputeBuffer(ref _meshObjectBVHBuffer, MeshBVH, BVHNodeSize);
        CreateComputeBuffer(ref _sphereBVHBuffer, SphereBVH, BVHNodeSize);
    }

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
            //RebuildTrees(RebuildObjectLists());
            RebuildObjectLists();
            RebuildTrees();
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