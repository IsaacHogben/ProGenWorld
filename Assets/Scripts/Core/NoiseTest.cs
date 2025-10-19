using UnityEngine;

public class FastNoiseTest : MonoBehaviour
{
    private FastNoise noise;

    void Start()
    {
        try
        {
            FastNoise fractal = new FastNoise("FractalFBm");
            fractal.Set("Source", new FastNoise("Simplex"));
            fractal.Set("Gain", 0.3f);
            fractal.Set("Lacunarity", 0.6f);

            for (int i = 0; i < 10; i++)
            {
                //Debug.Log(fractal.);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError("FastNoise failed: " + e.Message);
        }
    }
}
