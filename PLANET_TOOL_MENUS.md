# Planet tool menu integration

SpaceRunner uses Planet Studio as the current sequential authoring window. The two older package editors now register under the shared maintenance menu:

- `Tools > Planet Terrain > Legacy > Planet Generator`
- `Tools > Planet Terrain > Legacy > Terrain Authoring`

The editor types, asset callbacks, GUIDs, rendering algorithms and saved data remain unchanged. SpaceRunner's tool registry opens the same editor types and no longer registers duplicate wrapper menus. The old `Window/Rendering` aliases have been removed.

Qualification: SpaceRunner's `OwnerAlignedFinal9` passed 10/10 Editor/GPU tests, including all registered current/legacy routes, selected-object native package publication and unchanged whole height/normal maps. All 169 PlanetRendering source files in that validation project matched the installed source. This change does not claim gameplay, visual acceptance or 60 FPS.

[Current Planet Studio guide](https://github.com/drdredd007/SpaceRunner/blob/master/Docs/Coordinates/PLANET_STUDIO_GUIDE.md) and [qualification evidence](https://github.com/drdredd007/SpaceRunner/tree/master/Docs/Coordinates/Validation/PlanetTools20261021).
