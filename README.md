# Unity Ray Tracing Attempt
This is a small first time attempt at ray tracing through compute shaders in Unity. This project was inspired by Sebastion Lague's video [here](https://youtu.be/Qz0KTGYJtUk?feature=shared). Currently it is very slow for complex scenes, I am still working on optimizations for large meshes and scenes with many objects.

## Plans:
 - Bounding Volume Optimization                     \(Work In Progress\)
    - Better BVH Building (perhaps looking at kd-trees)
    - Allow for multiple primitives in 1 node (overlapping objects)
    - Split up larger meshes into smaller triangle groups
    - Setup BVH for all primitives regardless of type
 - Blended Normals                                  \(Not Started\)
 - Depth of field                                   \(Not Started\)
 - Liquids and transparency                         \(Not Started\)
 - Smoke and Heat Distortion                        \(Not Started\)

## Known Issues
 - Bounding Volume Heirarchy does not account for objects with overlapping bounds and sometimes leaves out objects due to poor splitting. (Will print warning message)

## Sources/Guides:
 - Basic Setup: [Ray Tracing Blog](http://blog.three-eyed-games.com/2018/05/03/gpu-ray-tracing-in-unity-part-1/)
 - Bounding Volumes: \(WIP\)