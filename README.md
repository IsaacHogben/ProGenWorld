# ProGenWorld

A high-performance, infinite procedurally generated voxel world system for Unity, featuring adaptive LOD management, flood-fill terrain expansion, and dynamic performance optimization.

<img width="1724" height="867" alt="image" src="https://github.com/user-attachments/assets/a40ba8ec-17b2-4a31-936b-cf0ac009c64a" />

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
<img width="940" alt="Domain warp mountains blending with plateau terrain" src="https://github.com/user-attachments/assets/0a82b02d-a4f3-45d5-be5c-55ca24877bd9" />

*System blends between large domain warp mountains and [Delve](https://github.com/IsaacHogben/Delve)-style plateaus*

---

### Pine Forest Biome
<img width="940" alt="Pine forest with procedurally animated player controller" src="https://github.com/user-attachments/assets/fa7e3c9f-9c34-4008-a6e1-cf33c7d01a0d" />

*Pine forest with the procedurally animated player controller*

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

