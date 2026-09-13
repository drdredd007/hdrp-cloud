# Procedural planet orbital prototype

This optional HDRP assembly owns procedural cube-sphere geometry and far-layer rendering.
It has no references to SpaceRunner, ECS physics or the application's coordinate graph.
An application adapter supplies a PlanetDefinition, camera position in the same double
reference frame, orientation, light direction, and one observer Camera.

Add PlanetFarPass to a global CustomPassVolume at BeforeTransparent. Assign both shaders
through serialized references so the Player build includes them. Supported initial path:
one perspective camera, D3D11, no XR, no dynamic resolution, no TAA. The application must
keep near-range rendering in the camera's normal HDRP coordinate system.

The pass starts with six root patches, then selects adaptive quadtree leaves using a
heuristic projected error, horizon priority, hysteresis and a bounded patch count.
Defaults: maximum level 8, 192 leaves, 4 pixel error and 4 new patches per update.
Each patch has 32 by 32 cells plus radial skirts; skirts are visual crack concealment,
not collision geometry or a watertight edge stitch. LOD geomorph is not implemented.
PlanetSurfaceCache keeps the complete old covering set until the replacement is ready,
then releases obsolete meshes. Transient residency can include both sets. Six initial
roots are synchronous and exempt from the generation budget. Subsequent Jobs/Burst
generation also completes within its update; fully asynchronous streaming is future work.
PlanetPatchKey and PlanetField are shared contracts for later surface refinement.
Height/normal samples depend on planet-local direction and seed, never the camera origin.

Positions are subtracted in double before conversion to a 1:1000 camera-relative layer.
Its RGBAFloat target stores exposed HDR RGB and **ray distance in metres** in alpha,
with an independent 24-bit depth attachment. The composition pass reconstructs near
distance from HDRP depth; it never compares raw depth values from different projections.
Memory scales with the active camera target size; buffers are replaced on resize and
released with the pass. Approximate target payload is 20 bytes per pixel, excluding driver
alignment. Each patch has 1,221 vertices and 2,304 triangles including skirts.

This is an orbital renderer, not a landing surface. It stops below 10 km; the example
application limits orbital camera navigation to 50 km. Surface colliders, near handoff,
terrain streaming, transparent far objects, temporal history, SSR/SSGI/DOF and atmosphere
are not implemented. Do not use this target as a replacement for the main HDRP depth.

PlanetDefinition.GeneratorVersion currently accepts only version 1. Different seeds or
radius/relief values rebuild geometry; center/orientation changes reuse meshes. The test
application carries CPU boundary/normal/precision tests and GPU capture fixtures.

## Editor authoring

Open Window > Rendering > HDRP Planet Generator and choose New Planet. The saved
PlanetGeneratorAsset contains seed, radius, relief, orientation and LOD settings.
Double-click an asset to reopen it. The native Inspector supports Undo/Redo; Save writes
only the selected asset. Drag the preview to orbit, scroll to zoom (minimum altitude
50 km), and use Live Preview or Refresh. Preview uses the same PlanetFarPass in an
isolated editor preview scene with neutral lighting; it pauses during Play and releases
its camera, render target and geometry on close/reload. It does not alter scene lighting
or save other dirty assets. Preview appearance still requires user visual acceptance.

The nested Editor assembly is editor-only and has no game dependencies. Assets are
generator presets, not coordinate-world instances. An application supplies placement
and instance identity and can reuse Definition/Lod/Orientation from an asset. Multiple
planet composition, coordinate authoring integration and surface editing remain future work.

## Local surface foundation

PlanetSurfaceAddress identifies a location by latitude/longitude, radial height and heading.
Longitude zero is planet-local +X and increases toward +Z; north is +Y. Heading 0 is north,
90 east. PlanetSurfaceCoordinates resolves a double local frame, independent of orbital
LOD and world center. Optional terrain alignment changes orientation only. The caller
applies planet instance pose and generator orientation separately.

PlanetLocalPatch builds metre-space arrays around this frame from the same height field.
Neighbouring patches share grid inputs. Positions, normals, colors and outward triangles
can feed a near renderer or an application's static mesh collider. Resolution is a power
of two from 2 to 128; patch coordinates are conservatively bounded to 8192 metres. The
returned object owns persistent native arrays and must be disposed. Vertex generation
uses Burst, but Build currently completes synchronously. This is not yet a near/far
handoff or asynchronous terrain streamer; the orbital camera restrictions still apply.
