using System.Numerics;
using Raylib_cs;

// Supersampling for non-Retina screens.
// On a 1x screen every edge (cars, kerbs, text) gets only one pixel's worth of detail.
// Here we draw the frame at double resolution into an off-screen image, then shrink it
// to the window. Each final pixel becomes the average of 2x2 drawn pixels, which gives
// much smoother edges. Retina screens already have the extra pixels, so they skip this.
static class Supersample
{
    const int Factor = 2;

    static RenderTexture2D target;
    static bool active;

    // Only worth it when the screen gives us 1 pixel per point
    static bool Wanted => Raylib.GetWindowScaleDPI().X < 1.5f;

    public static void Begin()
    {
        active = Wanted;
        if (!active)
        {
            Raylib.BeginDrawing();
            return;
        }

        int w = Raylib.GetRenderWidth() * Factor, h = Raylib.GetRenderHeight() * Factor;
        if (target.Texture.Width != w || target.Texture.Height != h)
        {
            if (target.Id != 0) Raylib.UnloadRenderTexture(target);
            target = Raylib.LoadRenderTexture(w, h);
            // Sampling exactly between 4 texels averages them: a clean 2x2 downscale
            Raylib.SetTextureFilter(target.Texture, TextureFilter.Bilinear);
        }

        Raylib.BeginDrawing();
        Raylib.BeginTextureMode(target);

        // Keep the game's coordinates (screen points) but spread them over the bigger image
        Rlgl.DrawRenderBatchActive();
        Rlgl.MatrixMode(MatrixMode.Projection);
        Rlgl.LoadIdentity();
        Rlgl.Ortho(0, Raylib.GetScreenWidth(), Raylib.GetScreenHeight(), 0, 0, 1);
        Rlgl.MatrixMode(MatrixMode.ModelView);
        Rlgl.LoadIdentity();
    }

    public static void End()
    {
        if (active)
        {
            Raylib.EndTextureMode();
            // Copy without blending: translucent UI left the image's alpha below 1,
            // and blending it onto the window would darken those areas
            Rlgl.DrawRenderBatchActive();
            Rlgl.DisableColorBlend();
            Texture2D tex = target.Texture;
            // Render textures are stored upside down, hence the negative height
            Raylib.DrawTexturePro(tex,
                new Rectangle(0, 0, tex.Width, -tex.Height),
                new Rectangle(0, 0, Raylib.GetScreenWidth(), Raylib.GetScreenHeight()),
                Vector2.Zero, 0, Color.White);
            Rlgl.DrawRenderBatchActive();
            Rlgl.EnableColorBlend();
        }
        Raylib.EndDrawing();
    }

    public static void Unload()
    {
        if (target.Id != 0) Raylib.UnloadRenderTexture(target);
    }
}
