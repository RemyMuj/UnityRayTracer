# Unity Ray Tracing Attempt
This is a small first time attempt at ray tracing through compute shaders in Unity. This project was inspired by Sebastion Lague's video [here](https://youtu.be/Qz0KTGYJtUk?feature=shared).

## Plans:
 - Bounding Volume Optimization                     \(Work In Progress\)
 - Depth of field                                   \(Not Started\)
 - Blended Normals                                  \(Not Started\)
 - Liquids and transparency                         \(Not Started\)
 - Smoke and Heat Distortion                        \(Not Started\)

## Known Issues
 - Bounding Volume Heirarchy not very effective \(likely due to problems below\)
 - Leaf bounds don't exclude object intersection tests.
 - Heirarchy does not account for objects with overlapping bounds.

## Sources/Guides:
 - Basic Setup: [Ray Tracing Blog](http://blog.three-eyed-games.com/2018/05/03/gpu-ray-tracing-in-unity-part-1/)
 - Bounding Volumes: \(WIP\)