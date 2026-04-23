# ProGenWorld

A high-performance, infinite procedurally generated voxel world system for Unity, featuring adaptive LOD management, flood-fill terrain expansion, and dynamic performance optimization.

<img width="6102" height="1145" alt="Disks-Transparent" src="https://github.com/user-attachments/assets/c494b9a3-5c95-4eae-9d78-4ec5f016182b" />

### Core Systems

- **Infinite Procedural Generation** - Seamlessly expanding voxel world generated on-the-fly
- **Multi-Biome Support** - Dynamic biome blending and terrain variation
- **3-Tier LOD System** - Automatic level-of-detail transitions for optimal performance
- **Flood-Fill Terrain Expansion** - Intelligent chunk generation that expands naturally from player position
- **Adaptive Performance Tuning** - Real-time adjustment of generation parameters based on system load

### Technical Highlights

- **Job System Integration** - Multi-threaded terrain generation using Unity's Job System and Burst compiler
- **Advanced Memory Management** - Object pooling for chunks, meshes, and native arrays
- **Cross-Chunk Decoration** - Trees and structures can span multiple chunks seamlessly
- **Async Pipeline Architecture** - Non-blocking generation pipeline with multiple processing stages
- **Smart Chunk Unloading** - Distance-based and memory-based chunk cleanup


<img width="3016" height="1189" alt="Red Landscape" src="https://github.com/user-attachments/assets/48bb2c95-6229-4d43-b5e7-994c6d5668c6" />

## Architecture

### Generation Pipeline

```
Player Movement
      ↓
Flood-Fill Frontier Expansion
      ↓
Biome Calculation
      ↓
Noise Generation
      ↓
Block Assignment
      ↓
Decoration System (Trees, Structures)
      ↓
Write System (Cross-chunk modifications)
      ↓
Mesh Generation
      ↓
Mesh Upload
      ↓
Chunk Rendering
```

### System Breakdown

**NoiseSystem**
- Generates 3D density fields for terrain using [FastNoise2](https://github.com/Auburn/FastNoise2)
- Async task-based processing
- Configurable noise parameters per biome

**BlockGenSystem**
- Converts density values to block IDs
- Unity Job System for parallelization
- Burst-compiled for performance

**DecorationSystem**
- Places features (trees, structures, ores)
- Cross-chunk decoration support
- Biome-aware feature placement

**WriteSystem**
- Manages block modifications across chunk boundaries
- Deferred write queue for chunks being generated
- Automatic neighbor chunk remeshing

**MeshSystem**
- Greedy meshing algorithm for optimal geometry
- LOD-aware mesh generation
- Pooled mesh and data buffers

**BiomeSystem**
- 3D noise-based biome and terrain-type distribution
- Smooth biome blending even between vastly different terrain types
- Per-biome terrain parameters including encoded node tree input from [FastNoise2](https://github.com/Auburn/FastNoise2)

## Features
### Flood-Fill Frontier Expansion
By checking the faces of our generated chunks we can determine which of its neighbours need to be generated. Only when a chunk face contains both a terrain block and an air block will the flood fill continue to generate chunks in that direction. This method prevents any unnecessary generation of chunks that will be comnpletely covered by other chunks or entirely made up of air.

### Two-layer block textures
Blocks have a 32 x 32 texture applied over 2 x 2 blocks using tri-planer mapping. A second texure is applied at a 1 pixel per block ratio. The final material is a blend of the two and creates satisfying variation across blocks of a single type.

## Screenshots

### Biome Blending and Atmospheric Effects
<img width="940" alt="Biome system blends between 3 different terrain types with early morning fog" src="https://github.com/user-attachments/assets/193e899e-b3c3-4b3c-915a-15fddc54c0be" />

*Biome system blends between 3 different terrain types and the atmospheric shaders create an early morning fog*

---

### Terrain Variety
<img width="2422" height="1143" alt="Cliff overlooking a large mushroom biome" src="https://github.com/user-attachments/assets/bb089a5e-3545-46c8-b3c8-726b2475494d" />

*System can support wildly different terrain types and blend between them*

---

### Fully Explorable
<img width="2311" height="1127" alt="Player finds old city in the desert" src="https://github.com/user-attachments/assets/0acb5872-b55c-43ce-a0ea-8ede85ae92db" />

*Explore with the procedurally animated player controller*

---

### LOD Comparison
<img width="940" alt="Near LOD showing high detail" src="https://github.com/user-attachments/assets/5125f4a2-d73f-493b-b4ee-720ed40d3e33" />
<img width="940" alt="Far LOD showing reduced detail" src="https://github.com/user-attachments/assets/ad8a7a12-20db-43cd-ac41-56176d8a9f4e" />

*Comparison of Far LOD (top) vs Near LOD (bottom) - demonstrating automatic detail reduction for performance optimization*

---

### Greedy Meshing Algorithm
<img width="940" alt="Close-up example of greedy meshing optimization" src="https://github.com/user-attachments/assets/0046adfd-45d4-4b34-a5eb-14ea68e04195" />
<img width="940" alt="Wireframe view showing mesh optimization" src="https://github.com/user-attachments/assets/cb7479bb-6265-4e14-8a40-642b9ede64c5" />

*Close-up example of Greedy Meshing algorithm - reduces triangle count by combining adjacent faces*

---

### Full World View
<img width="940" alt="14 chunk radius world with LOD optimization" src="https://github.com/user-attachments/assets/631d4d1b-9928-4c84-aa38-4879efdc7a90" />

*A 14 chunk radius world made up of 64³ sized chunks, optimized using LODs and Greedy Meshing*

---

### Performance Monitoring
<img width="273" alt="On-screen performance debugger showing adaptive control metrics" src="https://github.com/user-attachments/assets/ecf7fc2f-a6fa-4c0b-9cb2-2355034388d7" />

*On-screen debugger for adaptive performance control system*

## Additional Screenshots
<img width="1208" height="650" alt="1" src="https://github.com/user-attachments/assets/7cb5fab8-5556-41b1-bd56-ae99e56c6d60" />
<img width="2151" height="1054" alt="2" src="https://github.com/user-attachments/assets/db4c5d18-adbb-4ad6-b589-37361bd08390" />
<img width="1651" height="898" alt="3" src="https://github.com/user-attachments/assets/eb6d363d-1f36-4207-9c63-da96017d8efb" />
<img width="2343" height="1093" alt="5" src="https://github.com/user-attachments/assets/3f591333-a4e3-4dfe-87b8-899728486e10" />
<img width="2047" height="924" alt="6" src="https://github.com/user-attachments/assets/1a2a4fdf-9757-4cd9-9652-6e68543a71cd" />
<img width="1654" height="924" alt="7" src="https://github.com/user-attachments/assets/ebf988cf-78fe-4703-af10-61581d9412e4" />
<img width="2273" height="923" alt="9" src="https://github.com/user-attachments/assets/de9bf2d5-6ee3-4af9-b685-2f112eedcd73" />
<img width="1627" height="1162" alt="91" src="https://github.com/user-attachments/assets/d27ac5c1-4a53-4923-99be-c478068441fb" />
<img width="2565" height="1083" alt="92" src="https://github.com/user-attachments/assets/07044ce8-4421-435a-b492-8b1a6a375eed" />
<img width="1913" height="1005" alt="93" src="https://github.com/user-attachments/assets/d157542e-7aec-49b4-b130-86820da3fb3b" />
<img width="1953" height="1079" alt="94" src="https://github.com/user-attachments/assets/8d213adb-fa4c-48bc-ad6e-2c8ba7b5106b" />
<img width="2062" height="1013" alt="96" src="https://github.com/user-attachments/assets/06bb0ad1-5405-4c0b-949f-c79ca2b1e10a" />
<img width="2198" height="1122" alt="97" src="https://github.com/user-attachments/assets/5fe74c3e-4296-49f0-a288-c869759fda34" />
<img width="2333" height="1130" alt="98" src="https://github.com/user-attachments/assets/338ba59b-295e-4752-ac8a-ece3345ee26c" />
<img width="2355" height="1127" alt="99" src="https://github.com/user-attachments/assets/f8972640-6f88-4254-919d-af5332a00798" />
