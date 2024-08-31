using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class RayTraceDebug : MonoBehaviour {
    /// Class Variables /// <summary>
    public string logName = "log";
    public int debugLevel = 2; // 0 - NONE, 1 - WARNINGS ONLY, 2 - BASIC, 3 - DETAILED
    public bool drawSphereTree;
    public bool drawMeshTree;
    public bool drawMouseRayTrace;
    public bool drawNormals;

    /// Awake(): Setup ///
    private void Awake() {
        // Setup Run heading for log file
        Log("================================\nRun: " + System.DateTime.Now + "\n================================", 0);
    }

    /// Log(): Write strings to log file ///   
    public int Log(string text, int level = 2) {
        // Debug Level Filtering
        if (level > debugLevel) return 1;

        // Writing
        StreamWriter writer = new StreamWriter("Debug/" + logName + ".txt", true);
        writer.WriteLine(text);
        writer.Close();

        // Default Return
        return 0;
    }

    /// DrawBox(): Draw Debug Box ///
    private void DrawBox(Vector3 min, Vector3 max) {
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

    /// DrawBVH(): Draw BVH bounds ///
    private void DrawBVH(List<RayTraceMaster.BVHNode> list, int depth, Color col, Color colChange, int index = 0) {
        if (depth > 0) {
            // Get node
            RayTraceMaster.BVHNode node = list[index];

            // Draw Bounding Volume
            Gizmos.color = col;
            DrawBox(node.vmin, node.vmax);

            // Draw info (position in tree list, index of object)
            GUIStyle style = new GUIStyle();
            style.normal.textColor = (col*2.0f + Color.white) / 3.0f;
            UnityEditor.Handles.Label((node.vmin + node.vmax) / 2.0f, "(" + index.ToString() + ", " + node.index.ToString() + ")", style);

            // Recursion downwards
            int step = (int) Mathf.Floor(Mathf.Pow(2, depth - 2));
            DrawBVH(list, depth - 1, col + colChange, colChange, index*2 + 1);
            DrawBVH(list, depth - 1, col + colChange, colChange, index*2 + 2);
        }
    }

    /// DrawBVHTree(): Fully Draw BVH Tree ///
    public int DrawBVHTree(List<RayTraceMaster.BVHNode> list, int depth, int type) {
        // Setup depending on tree type
        Color startCol, endCol;
        switch(type) {
            case 0: // Mesh BVH Tree
                if (!drawMeshTree) return 1;
                startCol = new Color(0.0f, 1.0f, 0.0f);
                endCol = new Color(1.0f, 1.0f, 0.0f);
                break;
            case 1: // Sphere BVH Tree
                if (!drawSphereTree) return 1;
                startCol = new Color(1.0f, 0.0f, 0.0f);
                endCol = new Color(0.0f, 1.0f, 1.0f);
                break;
            default: // Invalid Tree Type
                return 1;
        }

        // Basic Drawing
        DrawBVH(list, depth, startCol, (endCol - startCol) / depth);

        // Mouse Ray Trace
        // BLANK

        // Default Return
        return 0;
    }

    /// DrawNormals(): Draw Normal Vectors ///
    public int DrawNormals(List<RayTraceMaster.MeshObject> _meshObjects, List<Vector3> _vertices, List<int> _indices, List<Vector3> _normals) {
        // Condition Check
        if (!drawNormals) return 1;

        // Draw Calculated Normals
        foreach (RayTraceMaster.MeshObject mesh in _meshObjects) {
            for (int i = mesh.indices_offset; i < mesh.indices_offset + mesh.indices_count; i++) {
                // White point at base
                Gizmos.color = Color.white;
                Gizmos.DrawSphere(mesh.localToWorldMatrix.MultiplyPoint3x4(_vertices[_indices[i]]), 0.005f);
                // Blue line indicating direction
                Gizmos.color = Color.blue;
                Gizmos.DrawLine(mesh.localToWorldMatrix.MultiplyPoint3x4(_vertices[_indices[i]]), mesh.localToWorldMatrix.MultiplyPoint3x4(_vertices[_indices[i]] + _normals[_indices[i]] * 0.1f));
            }
        }

        // Default Return
        return 0;
    }
}