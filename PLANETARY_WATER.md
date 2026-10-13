# Planetary HDRP water

The customized HDRP water pipeline supports camera-owned spherical oceans through `WaterGeometryType.Planetary` and `PlanetaryWaterRendering`. `PlanetHdrpWaterRenderer` supplies the state from an immutable EarthLike surface recipe and an authored `WaterSurface` prefab; no planet-sized float Transform is required.

Native FFT displacement, water Shader Graph, foam, GBuffer and deferred lighting remain in use. Ray/sphere intersections use double precision, while a separate R32 metric endpoint survives projection scaling for lighting, atmospheric composition and underwater absorption. Surface normals, refraction, environment reflection and light scattering use the radial up direction. Stock planar water keeps its original branch.

The screen carrier uses direct clip-space vertices inside the depth range. Physical camera depth is restored before native transparent objects; composed planet endpoints preserve foreground bodies. Canonical seabed intersections prevent seeing the opposite ocean through the solid body from underwater. Endpoint validity is reset for each observer render.

The qualified target is PC/Direct3D 11, one view per camera. Canonical height coverage applies to global Foundation roots; regional/detail/stamp terrain uses native opaque occlusion. Global fluid simulation, spherical CPU buoyancy queries, globalized planar masks/decals and XR remain separate work.

Setup, screenshots, executable controls and failure history are documented in [SpaceRunner planetary water](https://github.com/drdredd007/SpaceRunner/blob/main/Docs/Coordinates/PLANET_HDRP_WATER_2026-10-13.md).
