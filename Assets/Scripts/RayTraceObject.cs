using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(MeshRenderer))]
[RequireComponent(typeof(MeshFilter))]
public class RayTraceObject : MonoBehaviour {
    /// Object Parameters ///
    [System.NonSerialized] public int type = 0; // 0 - Mesh, 1 - Sphere
    [System.NonSerialized] public Bounds bounds;

    public Color albedoColor = new Color(0.0f, 0.4f, 1.0f);
    public Color specularColor = new Color(0.7f, 0.0f, 1.0f);
    public Color emissionColor = new Color(0.0f, 0.0f, 0.0f);
    public float smoothness = 0.69f;

    // Sphere Specific
    [System.NonSerialized] public float radius;
    [System.NonSerialized] public Vector3 position;

    /// OnEnable() ///
    private void OnEnable() {
        // setup General Properties
        bounds = GetComponent<Collider>().bounds;

        // Setup Properties based on type of Ray Trace Object        
        if (GetComponent<SphereCollider>() != null) {
            // Setup Sphere Properties
            type = 1;
            gameObject.layer = LayerMask.NameToLayer("RayTrace_sphere");

            radius = GetComponent<SphereCollider>().radius*Mathf.Max(transform.lossyScale.x, transform.lossyScale.y, transform.lossyScale.z);
            position = GetComponent<Transform>().position;
        } else {
            // Setup Mesh Properties
            gameObject.layer = LayerMask.NameToLayer("RayTrace_mesh");
        }

        // Register self
        RayTraceMaster.RegisterObject(this);
    }

    /// OnDisable() ///
    private void OnDisable() {
        RayTraceMaster.UnregisterObject(this);
    }
}