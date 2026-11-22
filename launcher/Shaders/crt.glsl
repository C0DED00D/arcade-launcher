#version 330
// Final CRT pass at the window's resolution.
// Scanline beams that widen with brightness, slot mask, curved glass, rounded tube corners, misconvergence,
// halation and glow, rolling hum, noise, power on/off, and the screen's glow reflected in the bezel.
in vec2 uv;
out vec4 o;

uniform sampler2D src;      // phosphor buffer, linear light (v = 0 is the top row)
uniform sampler2D bloomA;   // half-res blur  (halation)
uniform sampler2D bloomB;   // 1/8-res blur   (wide glow, bezel reflection)
uniform vec2  srcSize;
uniform vec4  tube;         // picture rectangle in window pixels: x, y (bottom-left), w, h
uniform float time;
uniform float power;        // 0 = off, 1 = on (animates the turn on / off)
uniform float lines;        // number of visible scanlines
uniform vec2  warp;         // barrel curvature x, y
uniform float scanStr;      // 0 = flat, 1 = full scanline beams
uniform float maskStr;      // shadow-mask strength
uniform float maskPitch;    // window pixels per mask sub-pixel
uniform float halation;
uniform float glow;
uniform float conv;         // misconvergence, in source pixels
uniform float noiseAmt;
uniform float bright;
uniform float cornerR;      // corner radius (fraction of picture height)
uniform float bezelRefl;

float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }

vec2 warpUV(vec2 t)
{
    vec2 c = t * 2.0 - 1.0;
    c *= vec2(1.0 + c.y * c.y * warp.x, 1.0 + c.x * c.x * warp.y);
    return c * 0.5 + 0.5;
}

// Sharpened horizontal fetch: crisp pixels without hard aliasing.
float fetch(vec2 p, int ch)
{
    float x = p.x * srcSize.x - 0.5;
    float xi = floor(x);
    float f = x - xi;
    f = clamp((f - 0.5) * 1.8 + 0.5, 0.0, 1.0);
    return texture(src, vec2((xi + 0.5 + f) / srcSize.x, p.y))[ch];
}

vec3 beam(vec2 p)
{
    float y = p.y * lines - 0.5;
    float y0 = floor(y);
    float fy = y - y0;
    vec3 sum = vec3(0.0);
    vec3 plain = vec3(0.0);
    for (int i = -1; i <= 2; i++)
    {
        float li = y0 + float(i);
        float v = (li + 0.5) / lines;
        if (v < 0.0 || v > 1.0) continue;
        float d = abs(fy - float(i));
        vec3 c = vec3(fetch(vec2(p.x + conv / srcSize.x, v), 0),
                      fetch(vec2(p.x, v), 1),
                      fetch(vec2(p.x - conv / srcSize.x, v), 2));
        // Brighter lines spread wider (the beam spot blooms); dark lines are thin with black gaps.
        vec3 w = mix(vec3(0.24), vec3(0.52), pow(c, vec3(0.6)));
        vec3 wt = exp(-0.5 * d * d / (w * w)) / (w * 2.5066);
        sum += c * wt;
        if (i == 0) plain += c * (1.0 - fy);
        if (i == 1) plain += c * fy;
    }
    return mix(plain, sum, scanStr);
}

vec3 slotMask(vec2 fc)
{
    float px = floor(fc.x / maskPitch);
    float py = floor(fc.y / maskPitch);
    float col = mod(px, 3.0);
    float triad = floor(px / 3.0);
    vec3 m = vec3(col < 0.5 ? 1.0 : 0.0, (col > 0.5 && col < 1.5) ? 1.0 : 0.0, col > 1.5 ? 1.0 : 0.0);
    float row = mod(py + mod(triad, 2.0) * 2.0, 4.0);
    if (row > 2.5) m *= 0.35; // the slot gaps, staggered every other triad
    vec3 dark = vec3(1.0 - maskStr);
    return mix(dark, vec3(1.0), m) / (1.0 - maskStr * 0.62);
}

// Signed distance to a rounded rectangle centred on the picture, in picture-height units.
float roundRect(vec2 p, float aspect, float r)
{
    vec2 q = abs((p - 0.5) * vec2(aspect, 1.0)) - (vec2(0.5 * aspect, 0.5) - r);
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

void main()
{
    vec2 fc = gl_FragCoord.xy;
    float aspect = tube.z / tube.w;
    vec2 t = vec2((fc.x - tube.x) / tube.z, 1.0 - (fc.y - tube.y) / tube.w); // 0..1, y down
    vec2 p = warpUV(t);

    // Turn on / off: a dot becomes a line, the line opens into the picture (and the reverse).
    float sx = clamp(power / 0.22, 0.004, 1.0);
    float sy = clamp((power - 0.18) / 0.82, 0.006, 1.0);
    sy = sy * sy * (3.0 - 2.0 * sy);
    vec2 pp = (p - 0.5) / vec2(sx, sy) + 0.5;
    float flash = (power < 0.999) ? clamp(1.0 / sqrt(sx * sy), 1.0, 7.0) : 1.0;

    float dTube = roundRect(p, aspect, cornerR);
    vec3 col = vec3(0.0);

    if (dTube < 0.0)
    {
        bool lit = all(greaterThanEqual(pp, vec2(0.0))) && all(lessThanEqual(pp, vec2(1.0))) && power > 0.001;
        if (lit)
        {
            col = beam(pp);
            col *= slotMask(fc);
            col += texture(bloomA, pp).rgb * halation;
            col += texture(bloomB, pp).rgb * glow;
            // Rolling hum bar and a faint flicker
            col *= 1.0 - 0.018 * (0.5 + 0.5 * sin((pp.y - time * 0.11) * 6.2832));
            col *= 1.0 + 0.006 * sin(time * 120.0);
            col *= bright * flash;
        }
        // Unlit phosphor is dark grey-green glass, not pure black
        col += vec3(0.010, 0.012, 0.011);
        // Vignette towards the tube edges
        float vig = 16.0 * p.x * p.y * (1.0 - p.x) * (1.0 - p.y);
        col *= mix(0.55, 1.0, pow(clamp(vig, 0.0, 1.0), 0.25));
        // Glass glare: a soft highlight upper left, picked up from the room
        float g = smoothstep(0.55, 0.0, length((t - vec2(0.22, 0.16)) * vec2(aspect, 1.0)));
        col += vec3(0.020, 0.022, 0.026) * g;
        // Noise
        col += (hash(fc * 0.731 + fract(time * 7.13)) - 0.5) * noiseAmt;
        // Soft anti-aliased tube edge
        col *= 1.0 - smoothstep(-0.004, 0.0, dTube);
    }
    if (dTube > -0.004)
    {
        // Bezel: dark matt plastic, lit by the screen's glow mirrored across the tube edge.
        vec2 m = t;
        m = mix(m, -m, step(m, vec2(0.0)));
        m = mix(m, 2.0 - m, step(vec2(1.0), m));
        float dist = max(dTube, 0.0);
        vec3 refl = texture(bloomB, clamp(m, 0.0, 1.0)).rgb * bezelRefl * exp(-dist * 22.0) * power;
        float shade = 0.010 + 0.008 * smoothstep(0.08, 0.0, dist);
        vec3 bezel = vec3(shade) + refl;
        // Inner lip of the bezel catches a little light
        bezel += vec3(0.018) * smoothstep(0.010, 0.002, abs(dist - 0.006));
        float outer = roundRect(t, aspect, cornerR + 0.05) - 0.05;
        bezel *= 1.0 - smoothstep(0.0, 0.004, outer);
        col = mix(bezel, col, 1.0 - smoothstep(-0.004, 0.0, dTube));
    }

    col = max(col, 0.0);
    col = pow(col, vec3(1.0 / 2.2));
    col += (hash(fc + 0.37) - 0.5) / 255.0; // dither
    o = vec4(col, 1.0);
}
