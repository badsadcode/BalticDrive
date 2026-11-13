using System;
using BalticDrive.World;
using Godot;

namespace BalticDrive.Sky;

/// <summary>
/// Sky, sun, clouds, cloud shadows, haze and the post-processing look (D23), ported from Thornwake's Atmosphere with Tom's
/// tuned Thornwake settings as the fixed values: the volumetric sky (Sky.gdshader, SkyCommon, CloudShapes), the same
/// cloud volume drawn in front of the scene (CloudsNear), light model 1 (sun colour and energy by elevation, warm sun
/// and cool shade, gentle eye adaptation), ACES with a "Fantasy" grade at 0.6, aerial haze and volumetric fog, sun
/// shafts and cloud shadows. Weather, rain, mist, moon and stars stay in Thornwake for now (the shaders already know them).
/// The sun is the real one over Mazowsze for <see cref="DayOfYear"/> and <see cref="Hour"/>.
/// </summary>
public partial class Atmosphere : Node3D
{
    public DirectionalLight3D Sun { get; private set; } = null!;
    public Godot.Environment Environment { get; private set; } = null!;

    /// <summary>Local solar time (hours) and day of the year (1 = 1 January). The clock stands still unless
    /// <see cref="TimeScale"/> is set (game hours per real second).</summary>
    public double Hour = 10.5, DayOfYear = 140;
    public double TimeScale;
    public double Latitude = 52.2;

    /// <summary>Cloud cover 0..1 (Tom's Thornwake "Clouds" 0.4) and afternoon towers 0..1.</summary>
    public float Coverage = 0.4f, Cumulus = 0.35f;

    private const double CloudPeriod = 6000, CloudHeight = 1600;
    private const float CloudBaseAboveSea = 1500;
    private const int Tile = 256;
    private ShaderMaterial _sky = null!, _cloudsNear = null!, _shafts = null!;
    private ShaderMaterial? _water;
    private DirectionalLight3D _moon = null!;
    private Decal _shadow = null!;
    private byte[] _noiseBase = null!;
    private float _shadowCoverage = -1;
    private double _windX, _windZ, _highX, _highZ, _lowX, _lowZ, _cloudTime;
    private float _adapted = 1;
    private const float BaseExposure = .95f * 1.15f;   // natural look at exposure 1
    private Vector2 _windDir = new Vector2(0.75f, 0.45f).Normalized();
    private const float WindSpeed = 5f;                // m/s at the ground

    public override void _Ready()
    {
        var noise = CloudNoise(Tile, 4242);
        var raw = noise.GetData();
        _noiseBase = new byte[Tile * Tile];
        for (int i = 0; i < _noiseBase.Length; i++) _noiseBase[i] = raw[i * 4];
        _sky = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/Sky/Sky.gdshader") };
        _cloudsNear = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/Sky/CloudsNear.gdshader"), RenderPriority = 99 };
        SkyParam("cloud_tex", ImageTexture.CreateFromImage(WithMipmaps(noise)));
        SkyParam("cloud_period", CloudPeriod);
        SkyParam("cloud_height", CloudHeight);
        SkyParam("cloud_noise", CloudNoise3D.Create());
        SkyParam("cloud_fine_noise", CloudNoise3D.CreateDetail());
        // Tom's Thornwake sky settings (rewrite_settings.cfg, 10 Nov 2026)
        SkyParam("quality", 3); SkyParam("cloud_blend", 1); SkyParam("cloud_look", 1); SkyParam("cloud_caps", 1);
        SkyParam("cloud_shape", 1); SkyParam("terrain_shade", 0); SkyParam("sea_broken", 0); SkyParam("cloud_body", 0);
        SkyParam("cloud_one_pass", 1); SkyParam("cloud_smooth", 1); SkyParam("layers_mode", 1); SkyParam("cloud_cost", 2);
        SkyParam("cloud_soft", 0); SkyParam("cloud_shading", 1); SkyParam("cloud_bases", 0); SkyParam("cloud_fine", 1);
        SkyParam("sea_tune", new Vector4(.5f, 0, 0, 0)); SkyParam("cloud_turbulence", 1); SkyParam("lens_style", 0);
        SkyParam("split_test", 0); SkyParam("sun_glare", .95f); SkyParam("sun_style", 0);
        _cloudsNear.SetShaderParameter("cloud_light_linear", 1);
        RenderingServer.GlobalShaderParameterSet("cloud_v2", 1f);

        Environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Godot.Sky { SkyMaterial = _sky, RadianceSize = Godot.Sky.RadianceSizeEnum.Size256, ProcessMode = Godot.Sky.ProcessModeEnum.Automatic },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            // the natural look (Thornwake fx.look = 1)
            TonemapMode = Godot.Environment.ToneMapper.Aces, TonemapWhite = 6, TonemapExposure = BaseExposure,
            FogEnabled = true, FogLightColor = new Color("aebfc2"), FogSunScatter = .15f, FogSkyAffect = .25f,
            FogDensity = .00007f, FogAerialPerspective = .26f,
            SsaoEnabled = true, SsaoRadius = 2.6f, SsaoIntensity = 4.5f, SsaoPower = 2.8f, SsaoDetail = 1.4f, SsaoHorizon = .04f,
            SsaoSharpness = .98f, SsaoLightAffect = .45f, SsaoAOChannelAffect = 1f,
            SsilEnabled = true, SsilRadius = 6, SsilIntensity = .55f,
            GlowEnabled = true, GlowIntensity = .6f * .55f, GlowHdrThreshold = 2.6f, GlowHdrScale = 1.2f, GlowBloom = 0,
            GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Additive, GlowHdrLuminanceCap = 4,
            // (Thornwake's .002–.0035 is tuned for views near the ground; Godot applies the fog at its 180 m length to everything
            // beyond, so over a 20 km landscape it veiled the whole view: thin, clear autumn air here, and less on the sky, D23)
            VolumetricFogEnabled = true, VolumetricFogDensity = .0011f, VolumetricFogLength = 180, VolumetricFogAnisotropy = .665f,
            VolumetricFogSkyAffect = .35f,
            VolumetricFogAlbedo = new Color("dfe6ea"), VolumetricFogAmbientInject = 1,
            AdjustmentEnabled = true, AdjustmentContrast = 1.06f, AdjustmentSaturation = 1.08f,
            AdjustmentColorCorrection = ColorGrading.Lut(2, .6f),
        };
        for (int level = 0; level < 7; level++)
            Environment.SetGlowLevel(level, level switch { 0 => .6f, 1 => .8f, 2 => 1, 3 => .5f, 4 => .15f, _ => 0 });
        AddChild(new WorldEnvironment { Environment = Environment });

        Sun = new DirectionalLight3D
        {
            Name = "Sun", LightColor = new Color("fff3df"), ShadowEnabled = true, DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits,
            DirectionalShadowMaxDistance = 700, DirectionalShadowSplit1 = .04f, DirectionalShadowSplit2 = .12f, DirectionalShadowSplit3 = .35f,
            DirectionalShadowBlendSplits = true, LightVolumetricFogEnergy = 1.12f,
        };
        AddChild(Sun);
        // A simple full moon for now (Thornwake's moon has phases and its own path): dim silver-blue light at night
        _moon = new DirectionalLight3D { Name = "Moon", LightColor = new Color("aebfe6"), ShadowEnabled = true, LightEnergy = 0, Visible = false,
            DirectionalShadowMaxDistance = 300, SkyMode = DirectionalLight3D.SkyModeEnum.LightOnly };
        AddChild(_moon);
        RenderingServer.DirectionalShadowAtlasSetSize(8192, true);
        RenderingServer.DirectionalSoftShadowFilterSetQuality(RenderingServer.ShadowQuality.SoftHigh);

        _shadow = new Decal
        {
            Size = new Vector3((float)CloudPeriod * 3, 8000, (float)CloudPeriod * 3),
            AlbedoMix = 1, Modulate = new Color(1, 1, 1, .31f), UpperFade = .02f, LowerFade = .02f, NormalFade = 0, CullMask = 1,
        };
        AddChild(_shadow);
        _shafts = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/Sky/SunShafts.gdshader"), RenderPriority = 100 };
        _shafts.SetShaderParameter("intensity", .6f);
        _shafts.SetShaderParameter("contrast", .15f);
        // the water (one shared material): its sky fallback and sun follow the hour
        _water = GD.Load<Material>("res://Shaders/water.tres") as ShaderMaterial;
    }

    private void SkyParam(string name, Variant value)
    {
        _sky.SetShaderParameter(name, value);
        _cloudsNear.SetShaderParameter(name, value);
    }

    private readonly System.Collections.Generic.HashSet<ulong> _attached = new();

    /// <summary>The full-screen passes ride on the camera that draws: the clouds in front of the scene and the sun shafts.</summary>
    public void AttachCamera(Camera3D camera)
    {
        if (!_attached.Add(camera.GetInstanceId())) return;
        foreach (var m in new Material[] { _cloudsNear, _shafts })
            camera.AddChild(new MeshInstance3D
            {
                Mesh = new QuadMesh { Size = new Vector2(2, 2) }, MaterialOverride = m, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                ExtraCullMargin = 16384, Position = new Vector3(0, 0, -1), GIMode = GeometryInstance3D.GIModeEnum.Disabled,
            });
    }

    /// <summary>Per frame. <paramref name="cameraLocal"/> is the drawing camera's scene position; world = scene + origin.</summary>
    public void Update(double delta, Camera3D? camera)
    {
        if (camera == null) return;
        AttachCamera(camera);
        float dt = (float)delta;
        Hour = (Hour + delta * TimeScale) % 24;
        var cameraLocal = camera.GlobalPosition;
        double originX = WorldOrigin.X, originZ = WorldOrigin.Z;
        double cameraWorldX = cameraLocal.X + originX, cameraWorldZ = cameraLocal.Z + originZ;

        var (elevation, azimuth) = SunPosition(DayOfYear, Hour, Latitude);
        var sunDir = Direction(Math.Max(elevation, -4), azimuth);
        Sun.Basis = Basis.LookingAt(-sunDir, Mathf.Abs(sunDir.Y) > .99f ? Vector3.Forward : Vector3.Up);
        float el = (float)elevation;

        var light = Light(el);
        Sun.LightEnergy = light.SunEnergy;
        Sun.LightColor = light.SunColor;
        Environment.AmbientLightEnergy = light.AmbientEnergy;
        Environment.AmbientLightSkyContribution = light.SkyShare;
        Environment.AmbientLightColor = light.ShadeTint;
        _adapted = Mathf.Lerp(_adapted, Exposure(light, el), 1 - Mathf.Exp(-dt / 1.5f));
        Environment.TonemapExposure = BaseExposure * _adapted;
        float daylight = Mathf.Clamp(el / 20, .08f, 1);
        float night = 1 - Mathf.SmoothStep(-10, 4, el);
        var (fog, scatter, nightFog) = Fog(el);
        Environment.FogLightColor = (fog * Mathf.Clamp(daylight * 1.1f, 0, 1)).Lerp(nightFog, night * .85f);
        Environment.FogSunScatter = scatter;

        var toSun = Sun.GlobalTransform.Basis.Z.Normalized();
        var trueSun = Direction(elevation, azimuth);
        RenderingServer.GlobalShaderParameterSet("sun_dir_world", toSun);
        RenderingServer.GlobalShaderParameterSet("sun_true", new Vector4(trueSun.X, trueSun.Y, trueSun.Z, el));
        var glow = Sun.LightColor * Sun.LightEnergy * Mathf.Clamp(toSun.Y * 6 + .2f, 0, 1);
        RenderingServer.GlobalShaderParameterSet("sun_glow", new Vector4(glow.R, glow.G, glow.B, 0));
        // the moon roughly opposite the sun (a full moon), 25–55° up through the night
        var moonDir = Direction(Mathf.Clamp(35 - el * .6f, 10, 55), (azimuth + 180) % 360);
        _moon.Basis = Basis.LookingAt(-moonDir, Vector3.Up);
        _moon.LightEnergy = .2f * night;
        _moon.Visible = _moon.LightEnergy > .002f;
        RenderingServer.GlobalShaderParameterSet("night_sky", new Vector4(moonDir.X, moonDir.Y, moonDir.Z, .5f));
        RenderingServer.GlobalShaderParameterSet("sky_extra", new Vector4((float)(DayOfYear + Hour / 24), 1, 1, .5f));
        RenderingServer.GlobalShaderParameterSet("wind", new Vector4(_windDir.X, WindSpeed / 8, _windDir.Y, .3f));
        RenderingServer.GlobalShaderParameterSet("caustic_light", Mathf.SmoothStep(2, 30, el));
        _shafts.SetShaderParameter("sun_direction", toSun);
        if (_water != null)
        {
            // Thornwake's UpdateWaterSky: the reflected sky darkens with the sun and warms its horizon at a low sun
            float day = Mathf.Clamp(Mathf.Sin(Mathf.DegToRad(Mathf.Max(el, -4))) * 4 + .35f, .08f, 1);
            var horizon = new Color(.68f, .75f, .78f);
            horizon = horizon.Lerp(horizon * new Color(1.25f, .95f, .75f), (1 - Mathf.SmoothStep(3, 25, el)) * (1 - night));
            var zenith = new Color(.29f, .45f, .62f);
            float dark = Mathf.Lerp(1, .12f, night);   // (a moonless night: Thornwake's moon is not ported yet)
            _water.SetShaderParameter("sky_horizon", horizon * day * dark);
            _water.SetShaderParameter("sky_zenith", zenith * day * dark);
            _water.SetShaderParameter("sun_direction", toSun);
            _water.SetShaderParameter("sun_color", Sun.LightColor * Sun.LightEnergy);
        }
        _shafts.SetShaderParameter("sun_color", Sun.LightColor);

        // clouds drift with the wind; layers on their own winds (high cirrus slow and veered, low scud fast)
        double wind = WindSpeed * 1.5 * 0.9;
        _windX = (_windX + delta * wind * _windDir.X) % CloudPeriod;
        _windZ = (_windZ + delta * wind * _windDir.Y) % CloudPeriod;
        var high = _windDir.Rotated(.9f);
        double highPeriod = CloudPeriod * 25, lowPeriod = CloudPeriod * .45 * 40;
        _highX = (_highX + delta * wind * .35 * high.X) % highPeriod; _highZ = (_highZ + delta * wind * .35 * high.Y) % highPeriod;
        _lowX = (_lowX + delta * wind * 1.9 * _windDir.X) % lowPeriod; _lowZ = (_lowZ + delta * wind * 1.9 * _windDir.Y) % lowPeriod;
        double cx = Wrap(cameraWorldX + _windX, CloudPeriod), cz = Wrap(cameraWorldZ + _windZ, CloudPeriod);
        RenderingServer.GlobalShaderParameterSet("cloud_state", new Vector4((float)cx, (float)cz, Coverage, 0));
        RenderingServer.GlobalShaderParameterSet("cloud_layers", new Vector4(
            (float)Wrap(cameraWorldX + _highX, highPeriod), (float)Wrap(cameraWorldZ + _highZ, highPeriod),
            (float)Wrap(cameraWorldX + _lowX, lowPeriod), (float)Wrap(cameraWorldZ + _lowZ, lowPeriod)));
        _cloudTime = (_cloudTime + delta) % 100000;
        RenderingServer.GlobalShaderParameterSet("cloud_types", new Vector4(Cumulus, 0, 0, 0));
        float baseAbove = Mathf.Min(CloudBaseAboveSea - cameraLocal.Y, 2200);
        RenderingServer.GlobalShaderParameterSet("cloud_more", new Vector4(0, Mathf.Atan2(_windDir.Y, _windDir.X), (float)_cloudTime, baseAbove));
        // the clouds in front of the scene are lit by the real sun and veiled like the sky
        RenderingServer.GlobalShaderParameterSet("cloud_light", new Vector4(Sun.LightColor.R, Sun.LightColor.G, Sun.LightColor.B, Sun.LightEnergy));
        RenderingServer.GlobalShaderParameterSet("cloud_light_dir", new Vector4(trueSun.X, trueSun.Y, trueSun.Z, 0));
        var fogLight = Environment.FogLightColor.SrgbToLinear() * Environment.FogLightEnergy;
        RenderingServer.GlobalShaderParameterSet("cloud_fog", new Vector4(fogLight.R, fogLight.G, fogLight.B, Environment.FogSkyAffect));
        RenderingServer.GlobalShaderParameterSet("cloud_fog2", new Vector4(Environment.FogSunScatter, Environment.FogAerialPerspective, 0, 0));
        _cloudsNear.SetShaderParameter("fog_depth", Environment.VolumetricFogLength * 1.02f + 1);

        // cloud shadows: ground point g is shaded by the cloud at g + toSun.xz · H / toSun.y (3×3 tiles of period P)
        if (Math.Abs(_shadowCoverage - Coverage) > 1e-4) RebuildShadowTexture(Coverage);
        double lift = Math.Max(toSun.Y, .25);
        double sx = toSun.X / lift * CloudHeight, sz = toSun.Z / lift * CloudHeight;
        double c0x = Wrap(CloudPeriod * .5 - (originX + sx + _windX), CloudPeriod), c0z = Wrap(CloudPeriod * .5 - (originZ + sz + _windZ), CloudPeriod);
        double dx = c0x + CloudPeriod * Math.Round((cameraLocal.X - c0x) / CloudPeriod);
        double dz = c0z + CloudPeriod * Math.Round((cameraLocal.Z - c0z) / CloudPeriod);
        _shadow.Position = new Vector3((float)dx, cameraLocal.Y, (float)dz);
        _shadow.Modulate = new Color(1, 1, 1, .31f * Mathf.SmoothStep(3, 18, el));
    }

    // ---- the sun ---------------------------------------------------------------------------------------------------

    /// <summary>Solar elevation and azimuth (degrees, azimuth from north through east) for local solar time.</summary>
    public static (double Elevation, double Azimuth) SunPosition(double dayOfYear, double hour, double latitude)
    {
        double decl = -23.44 * Math.Cos(Math.Tau / 365 * (dayOfYear + 10));
        double lat = Mathf.DegToRad((float)latitude), d = Mathf.DegToRad((float)decl), h = Mathf.DegToRad((float)((hour - 12) * 15));
        double sinEl = Math.Sin(lat) * Math.Sin(d) + Math.Cos(lat) * Math.Cos(d) * Math.Cos(h);
        double elev = Math.Asin(Math.Clamp(sinEl, -1, 1));
        double az = Math.Atan2(Math.Sin(h), Math.Cos(h) * Math.Sin(lat) - Math.Tan(d) * Math.Cos(lat)) + Math.PI;
        return (elev * 180 / Math.PI, az * 180 / Math.PI);
    }

    /// <summary>Unit vector toward a point in the sky (world: x east, y up, z south).</summary>
    public static Vector3 Direction(double elevation, double azimuth)
    {
        double el = Mathf.DegToRad((float)elevation), az = Mathf.DegToRad((float)azimuth);
        return new Vector3((float)(Math.Cos(el) * Math.Sin(az)), (float)Math.Sin(el), (float)(-Math.Cos(el) * Math.Cos(az)));
    }

    // ---- light model 1 (Thornwake AtmosphereLight + the temperate and plains light profiles, half each) -------------

    private readonly record struct LightState(Color SunColor, float SunEnergy, float AmbientEnergy, Color ShadeTint, float SkyShare);

    private static readonly (float E, float V)[] SunEnergyCurve =
        { (-4, .02f), (0, .16f), (5, .42f), (10, .68f), (20, .92f), (35, 1.12f), (60, 1.28f), (90, 1.32f) };
    private static readonly (float E, float V)[] SkyEnergyCurve =
        { (-6, .2f), (0, .24f), (10, .33f), (35, .40f), (60, .43f), (90, .44f) };
    private static readonly (float E, Color C)[] SunColorCurve =
    {
        (-4, new Color("ff8e52")), (0, new Color("ffa262")), (5, new Color("ffb978")), (10, new Color("ffcf98")),
        (20, new Color("ffe2bd")), (35, new Color("fff0dc")), (60, new Color("fff6ea")), (90, new Color("fff8ee")),
    };
    // Mazowsze: fields, woods and towns — half Thornwake's temperate-forest profile, half its plains
    private static readonly (float E, Color C)[] ShadeCurve =
    {
        (-10, new Color("31617f")), (-4, new Color("7479c8")), (2, new Color("ad9ad7")), (8, new Color("a8bedf")),
        (20, new Color("a9d4e7")), (90, new Color("b6dced")),
    };
    private static readonly (float E, Color C)[] FogCurve =
    {
        (-4, new Color("9484b7")), (2, new Color("bda1c4")), (10, new Color("bcbaca")), (25, new Color("aec5d4")), (90, new Color("acc7d8")),
    };
    private static readonly Color NightFog = new("102a3b"), SunTint = new("fff9ee");
    private const float ShadeShare = .29f, NightShare = .5f, Scatter = 1.08f;

    private static LightState Light(float elevation)
    {
        float sun = Curve(elevation, SunEnergyCurve);
        var color = Curve(elevation, SunColorCurve) * SunTint;
        float sky = Curve(elevation, SkyEnergyCurve);
        var shade = Curve(elevation, ShadeCurve);
        float share = 1 - Mathf.Lerp(NightShare, ShadeShare, Mathf.SmoothStep(-8, 0, elevation));
        // (Thornwake's night darkness "normal" and the 0.45 storm/forest darkness have no effect on a clear open sky)
        return new LightState(color, sun, sky, shade, share);
    }

    private static float Exposure(in LightState light, float elevation)
    {
        float key = (light.SunEnergy * .75f + light.AmbientEnergy * 1.6f) / (1.3f * .75f + .44f * 1.6f);
        float day = Mathf.Clamp(Mathf.Pow(Mathf.Max(key, .05f), -.25f), 1, 1.19f);
        day = Mathf.Lerp(1, day, Mathf.SmoothStep(-4, 2, elevation));
        day *= Mathf.Lerp(1, 1.19f, Mathf.SmoothStep(-1, -8, elevation));
        return Mathf.Clamp(day, 1, 1.5f);
    }

    private static (Color Color, float SunScatter, Color Night) Fog(float elevation)
    {
        float scatter = Mathf.Lerp(.42f, .2f, Mathf.SmoothStep(15, 50, elevation)) * Scatter;
        return (Curve(elevation, FogCurve), scatter, NightFog);
    }

    private static float Curve(float x, (float E, float V)[] t)
    {
        if (x <= t[0].E) return t[0].V;
        for (int i = 1; i < t.Length; i++)
            if (x <= t[i].E) return Mathf.Lerp(t[i - 1].V, t[i].V, Mathf.SmoothStep(t[i - 1].E, t[i].E, x));
        return t[^1].V;
    }

    private static Color Curve(float x, (float E, Color C)[] t)
    {
        if (x <= t[0].E) return t[0].C;
        for (int i = 1; i < t.Length; i++)
            if (x <= t[i].E) return t[i - 1].C.Lerp(t[i].C, Mathf.SmoothStep(t[i - 1].E, t[i].E, x));
        return t[^1].C;
    }

    private static double Wrap(double v, double period) => v - Math.Floor(v / period) * period;

    // ---- cloud textures (Thornwake Atmosphere) -----------------------------------------------------------------------

    private void RebuildShadowTexture(float coverage)
    {
        _shadowCoverage = coverage;
        var image = Image.CreateEmpty(Tile * 3, Tile * 3, false, Image.Format.Rgba8);
        float edge = 1 - coverage;
        var alpha = new byte[Tile * Tile];
        for (int y = 0; y < Tile; y++)
            for (int x = 0; x < Tile; x++)
            {
                float n = _noiseBase[y * Tile + x] / 255f * .8f + .5f * .2f;
                alpha[y * Tile + x] = (byte)(Mathf.Clamp(Mathf.SmoothStep(edge - .05f, edge + .2f, n), 0, 1) * 255);
            }
        var data = new byte[Tile * 3 * Tile * 3 * 4];
        for (int y = 0; y < Tile * 3; y++)
            for (int x = 0; x < Tile * 3; x++)
            {
                int i = (y * Tile * 3 + x) * 4;
                data[i] = 0; data[i + 1] = 0; data[i + 2] = 8; data[i + 3] = alpha[(y % Tile) * Tile + x % Tile];
            }
        image.SetData(Tile * 3, Tile * 3, false, Image.Format.Rgba8, data);
        image.GenerateMipmaps();
        _shadow.TextureAlbedo = ImageTexture.CreateFromImage(image);
    }

    internal static Image WithMipmaps(Image image)
    {
        var copy = (Image)image.Duplicate();
        copy.GenerateMipmaps();
        return copy;
    }

    /// <summary>Tileable cloud noise: R = billowy base, G = finer detail (Thornwake's, deterministic).</summary>
    internal static Image CloudNoise(int size, uint seed)
    {
        var data = new byte[size * size * 4];
        float Lattice(int x, int y, int period, uint salt)
        {
            x = ((x % period) + period) % period; y = ((y % period) + period) % period;
            uint h = (uint)x * 374761393u + (uint)y * 668265263u + salt * 2246822519u + seed;
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }
        float Value(float u, float v, int period, uint salt)
        {
            float x = u * period, y = v * period;
            int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy);
            float a = Lattice(x0, y0, period, salt), b = Lattice(x0 + 1, y0, period, salt);
            float c = Lattice(x0, y0 + 1, period, salt), d = Lattice(x0 + 1, y0 + 1, period, salt);
            return a + (b - a) * fx + (c - a) * fy + (a - b - c + d) * fx * fy;
        }
        float Fbm(float u, float v, int period, int octaves, uint salt)
        {
            float sum = 0, amp = 1, norm = 0;
            for (int o = 0; o < octaves; o++) { sum += amp * Value(u, v, period << o, salt + (uint)o); norm += amp; amp *= .5f; }
            return sum / norm;
        }
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float u = x / (float)size, v = y / (float)size;
                float n = Fbm(u, v, 4, 4, 1);
                float b = 1 - MathF.Abs(2 * Fbm(u, v, 8, 3, 9) - 1);
                float baseValue = Math.Clamp((n * .75f + b * .25f - .2f) / .6f, 0, 1);
                float detail = Fbm(u, v, 8, 4, 21);
                int i = (y * size + x) * 4;
                data[i] = (byte)(baseValue * 255); data[i + 1] = (byte)(detail * 255); data[i + 2] = 0; data[i + 3] = 255;
            }
        return Image.CreateFromData(size, size, false, Image.Format.Rgba8, data);
    }
}
