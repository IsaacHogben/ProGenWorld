using Unity.Burst;
using Unity.Mathematics;

// ----------------------------------------------------------------------------
// Shape style enum — usable for foliage, rock clusters, crystal formations,
// or any other blob/radiating/crown-type silhouette placed at a point.
// ----------------------------------------------------------------------------
public enum ShapeStyle : byte
{
    None = 0,
    Sphere = 1,
    Cube = 2,
    SquareFlat = 3,
    CircleFlat = 4,
    HangingHemisphere = 5,
    Cluster = 6,
    PalmCrown = 7,
    LineDown = 8,
}

// ----------------------------------------------------------------------------
// Shape placer  — Burst-safe
// ----------------------------------------------------------------------------
// All styles take `size` as the shape's overall DIAMETER in blocks (or side
// length, for the square/cube variants). Internally each style converts that
// to whatever radii/extents its silhouette needs.
[BurstCompile]
public static class ShapePlacer
{
    [BurstCompile]
    public static void Place(
        ShapeStyle style,
        BlockType block,
        int size,
        int3 pos,
        int branchDepth,
        int maxDepth,
        ref Unity.Mathematics.Random rng,
        ApplyBlockDelegate applyBlock)
    {
        switch (style)
        {
            case ShapeStyle.HangingHemisphere: PlaceHangingHemisphere(pos, size, block, ref rng, applyBlock); break;
            case ShapeStyle.Cluster: PlaceCluster(pos, size, block, ref rng, applyBlock); break;
            case ShapeStyle.PalmCrown: PlacePalmCrown(size, pos, block, ref rng, applyBlock); break;
            case ShapeStyle.Sphere: PlaceSphere(pos, size, block, applyBlock); break;
            case ShapeStyle.Cube: PlaceCube(pos, size, block, applyBlock); break;
            case ShapeStyle.SquareFlat: PlaceSquareFlat(pos, size, block, applyBlock); break;
            case ShapeStyle.CircleFlat: PlaceCircleFlat(pos, size, block, applyBlock); break;
            case ShapeStyle.LineDown: PlaceLineDown(pos, size, block, applyBlock); break;
        }
    }

    // Hemisphere of blocks above the tip, with randomised downward-hanging strands.
    // size = diameter of the hemisphere's equator. R = size/2 reproduces the
    // original fixed-R3 look at size=7 (R = 7/2 = 3, integer division).
    private static void PlaceHangingHemisphere(int3 p, int size, BlockType block, ref Unity.Mathematics.Random rng, ApplyBlockDelegate ab)
    {
        int x = p.x, y = p.y, z = p.z;
        int R = math.max(1, size / 2);

        // Upper hemisphere (dy >= 0)
        for (int dx = -R; dx <= R; dx++)
            for (int dy = 0; dy <= R; dy++)
                for (int dz = -R; dz <= R; dz++)
                {
                    float dist = math.sqrt(dx * dx + dy * dy + dz * dz);
                    if (dist <= R + 0.5f && rng.NextFloat() > 0.15f)
                        ab(x + dx, y + dy, z + dz, block);
                }

        // Hanging strands — random lengths dropping below the tip
        for (int dx = -R; dx <= R; dx++)
            for (int dz = -R; dz <= R; dz++)
            {
                // Only hang from the outer edge of the hemisphere base
                float edgeDist = math.sqrt(dx * dx + dz * dz);
                if (edgeDist > R - 0.5f && edgeDist <= R + 0.5f && rng.NextFloat() > 0.4f)
                {
                    int hangLen = rng.NextInt(1, 5);
                    for (int dy = -1; dy >= -hangLen; dy--)
                        ab(x + dx, y + dy, z + dz, block);
                }
            }
    }

    // Sparse wispy cluster — a small filled core plus radiating fingers.
    // size = diameter of the overall cluster including arms.
    // outerRadius = size/2; core is a fraction of that, arms fill the rest.
    private static void PlaceCluster(int3 p, int size, BlockType block, ref Unity.Mathematics.Random rng, ApplyBlockDelegate ab)
    {
        int x = p.x, y = p.y, z = p.z;

        int outerRadius = math.max(2, size / 2);
        int coreRadius = math.max(1, outerRadius / 2);
        int coreRadiusSq = coreRadius * coreRadius + 1; // +1 keeps the same "<=" feel as the old fixed radius-2 check (dx²+dy²+dz²<=5)

        // Core — small filled ball at the tip
        for (int dx = -coreRadius; dx <= coreRadius; dx++)
            for (int dy = -1; dy <= coreRadius; dy++)
                for (int dz = -coreRadius; dz <= coreRadius; dz++)
                    if (dx * dx + dy * dy + dz * dz <= coreRadiusSq && rng.NextFloat() > 0.2f)
                        ab(x + dx, y + dy, z + dz, block);

        // Sparse radiating fingers — 5-8 directions, length scaled off outerRadius
        int armMin = math.max(1, outerRadius / 3);
        int armMax = math.max(armMin + 1, outerRadius);

        int arms = rng.NextInt(5, 9);
        for (int a = 0; a < arms; a++)
        {
            float angle = (a / (float)arms) * math.PI * 2f + rng.NextFloat() * 0.7f;
            int len = rng.NextInt(armMin, armMax + 1);
            int armDx = (int)math.round(math.cos(angle) * len);
            int armDz = (int)math.round(math.sin(angle) * len);
            int armDy = rng.NextInt(-1, 3); // slight vertical scatter

            // Fill every block along the arm — no gaps so no floating pieces
            for (int i = 1; i <= len; i++)
            {
                float t = i / (float)len;
                int bx = x + (int)math.round(armDx * t);
                int bz = z + (int)math.round(armDz * t);
                int by = y + armDy;
                ab(bx, by, bz, block);
                // Optional second layer on the lower half of each arm for volume
                if (t < 0.5f && rng.NextFloat() > 0.4f)
                    ab(bx, by + 1, bz, block);
            }
        }
    }

    // size = diameter of the crown (fronds radiate out to roughly size/2 + droop).
    private static void PlacePalmCrown(int size, int3 p, BlockType block,
        ref Unity.Mathematics.Random rng, ApplyBlockDelegate ab)
    {
        int x = p.x, y = p.y, z = p.z;
        int crownRadius = math.max(1, size / 2);

        // Tight central tuft
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = 0; dy <= 4; dy++)
                for (int dz = -1; dz <= 1; dz++)
                    if (rng.NextFloat() > 0.1f)
                        ab(x + dx, y + dy, z + dz, block);

        int numFronds = rng.NextInt(crownRadius + 3, crownRadius + 10);
        for (int f = 0; f < numFronds; f++)
        {
            float angle = (f / (float)numFronds) * math.PI * 2f + rng.NextFloat() * 0.3f;
            float cosA = math.cos(angle);
            float sinA = math.sin(angle);
            int frondLen = rng.NextInt(crownRadius, crownRadius + 5);

            // Starting direction from pitch angle
            float frondPitch = rng.NextFloat() * math.PI * 0.5f + math.PI * 0.25f;

            // Initial step direction vector
            float3 dir = new float3(cosA * math.sin(frondPitch),
                                    -math.cos(frondPitch),
                                    sinA * math.sin(frondPitch));

            float perpCos = math.cos(angle + math.PI * 0.5f);
            float perpSin = math.sin(angle + math.PI * 0.5f);

            float3 pos = new float3(x, y + 1, z);

            for (int i = 1; i <= frondLen; i++)
            {
                float t = i / (float)frondLen;

                // Bend direction downward each step — same curve regardless of starting angle
                dir.y -= 0.18f;
                dir = math.normalize(dir);

                pos += dir;

                int fx = (int)math.round(pos.x);
                int fy = (int)math.round(pos.y);
                int fz = (int)math.round(pos.z);

                ab(fx, fy, fz, block);
                ab(fx, fy + 1, fz, block);

                float leafWidth = 1.5f * math.sin(t * math.PI);
                int leafExt = (int)math.round(leafWidth);
                for (int l = -leafExt; l <= leafExt; l++)
                {
                    int lx = fx + (int)math.round(perpCos * l);
                    int lz = fz + (int)math.round(perpSin * l);
                    ab(lx, fy, lz, block);
                    if (t < 0.7f && rng.NextFloat() > 0.2f)
                        ab(lx, fy + 1, lz, block);
                }

                if (t > 0.5f && rng.NextFloat() > 0.5f)
                    ab(fx, fy - 1, fz, block);
            }
        }
    }

    // Filled sphere. size = diameter, centered on p.
    private static void PlaceSphere(int3 p, int size, BlockType block, ApplyBlockDelegate ab)
    {
        int x = p.x, y = p.y, z = p.z;
        float r = size * 0.5f;
        float rSq = r * r;
        int ext = (int)math.ceil(r);

        for (int dx = -ext; dx <= ext; dx++)
            for (int dy = -ext; dy <= ext; dy++)
                for (int dz = -ext; dz <= ext; dz++)
                {
                    // +0.25 softens the surface slightly, matching the rounding
                    // tolerance used elsewhere (e.g. PlaceRoundSlice's circle test).
                    if (dx * dx + dy * dy + dz * dz <= rSq + 0.25f)
                        ab(x + dx, y + dy, z + dz, block);
                }
    }

    // Filled cube. size = side length, centered on p (odd sizes center exactly
    // on a block; even sizes bias one extra layer toward +axis).
    private static void PlaceCube(int3 p, int size, BlockType block, ApplyBlockDelegate ab)
    {
        int x = p.x, y = p.y, z = p.z;
        int half = math.max(1, size) / 2;
        int lo = -half;
        int hi = size - 1 + lo; // ensures span == size regardless of odd/even

        for (int dx = lo; dx <= hi; dx++)
            for (int dy = lo; dy <= hi; dy++)
                for (int dz = lo; dz <= hi; dz++)
                    ab(x + dx, y + dy, z + dz, block);
    }

    // Flat filled square, one block thick, lying in the horizontal (XZ) plane.
    // size = side length, centered on p.
    private static void PlaceSquareFlat(int3 p, int size, BlockType block, ApplyBlockDelegate ab)
    {
        int x = p.x, y = p.y + 1, z = p.z;  // Ensure shape appears above branch with a +1 on y
        int half = math.max(1, size) / 2;
        int lo = -half;
        int hi = size - 1 + lo;

        for (int dx = lo; dx <= hi; dx++)
            for (int dz = lo; dz <= hi; dz++)
                ab(x + dx, y, z + dz, block);
    }

    // Flat filled circle, one block thick, lying in the horizontal (XZ) plane.
    // size = diameter, centered on p.
    private static void PlaceCircleFlat(int3 p, int size, BlockType block, ApplyBlockDelegate ab)
    {
        int x = p.x, y = p.y + 1, z = p.z;  // Ensure shape appears above branch with a +1 on y
        float r = size * 0.5f;
        float rSq = r * r;
        int ext = (int)math.ceil(r);

        for (int dx = -ext; dx <= ext; dx++)
            for (int dz = -ext; dz <= ext; dz++)
                if (dx * dx + dz * dz <= rSq + 0.25f)
                    ab(x + dx, y, z + dz, block);
    }

    // Straight line of blocks descending from p. size = number of blocks placed,
    // starting at p itself and going downward (size = 1 places just p).
    private static void PlaceLineDown(int3 p, int size, BlockType block, ApplyBlockDelegate ab)
    {
        int count = math.max(1, size);
        for (int i = 0; i < count; i++)
            ab(p.x, p.y - i, p.z, block);
    }
}