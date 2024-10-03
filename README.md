# Unity Ray Tracing Attempt
This is a small first time attempt at ray tracing through compute shaders in Unity. This project was inspired by Sebastion Lague's video [here](https://youtu.be/Qz0KTGYJtUk?feature=shared). Currently it is very slow for complex scenes, I am still working on optimizations for large meshes and scenes with many objects.

## Plans:
 - Bounding Volume Optimization                     \(Work In Progress\)
    - Better pairing choices when building BVH Tree
    - Split up larger meshes into smaller triangle groups
 - Blended Normals                                  \(Work In Progress\)
    - Fix strange blending on sharp corners
 - Depth of field                                   \(Not Started\)
 - Liquids and transparency                         \(Not Started\)
 - Smoke and Heat Distortion                        \(Not Started\)

## Known Issues
 - Bounding Volume Heirarchy does not always choose best splitting \(O\(n!\) problem\)
 - BVH Building sometimes leaves out Mesh Objects.
 - Normal blending glitches on low-poly objects with sharp corners

## Sources/Guides:
 - Basic Setup: [Ray Tracing Blog](http://blog.three-eyed-games.com/2018/05/03/gpu-ray-tracing-in-unity-part-1/)
 - Normal Blending: [Lesson on Barycentric Coordinates](https://www.scratchapixel.com/lessons/3d-basic-rendering/ray-tracing-rendering-a-triangle/barycentric-coordinates.html)