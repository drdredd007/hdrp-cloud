# Procedural planet orbital prototype

This optional HDRP assembly owns procedural cube-sphere geometry and far-layer rendering.
It has no references to SpaceRunner, ECS physics or the application's coordinate graph.
An application adapter supplies a PlanetDefinition, camera position in the same double
reference frame, orientation, light direction, and one observer Camera.

Add PlanetFarPass to a global CustomPassVolume at BeforeTransparent. Assign both shaders
through serialized references so the Player build includes them. Supported initial path:
one perspective camera, D3D11, no XR, no dynamic resolution, no TAA. The application must
keep near-range rendering in the camera's normal HDRP coordinate system.

The pass generates 96 immutable patches (level 2, 32 by 32 cells) on first execution.
Generation uses Jobs/Burst but waits synchronously; streaming and adaptive LOD are future work.
PlanetPatchKey and PlanetField are shared contracts for later surface refinement.
Height/normal samples depend on planet-local direction and seed, never the camera origin.

Positions are subtracted in double before conversion to a 1:1000 camera-relative layer.
Its RGBAFloat target stores exposed HDR RGB and **ray distance in metres** in alpha,
with an independent 24-bit depth attachment. The composition pass reconstructs near
distance from HDRP depth; it never compares raw depth values from different projections.
Memory scales with the active camera target size; buffers are replaced on resize and
released with the pass. Approximate target payload is 20 bytes per pixel, excluding driver
alignment. Geometry has approximately 105k vertices / 197k triangles.

This is an orbital renderer, not a landing surface. It stops below 10 km; the example
application limits orbital camera navigation to 50 km. Surface colliders, adaptive LOD,
terrain streaming, transparent far objects, temporal history, SSR/SSGI/DOF and atmosphere
are not implemented. Do not use this target as a replacement for the main HDRP depth.

PlanetDefinition.GeneratorVersion currently accepts only version 1. Different seeds or
radius/relief values rebuild geometry; center/orientation changes reuse meshes. The test
application carries CPU boundary/normal/precision tests and GPU capture fixtures.
