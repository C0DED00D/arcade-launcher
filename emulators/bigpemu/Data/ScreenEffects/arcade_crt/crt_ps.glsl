#version 430
// Arcade launcher CRT, final pass (a port of the launcher menu's CRT shader, so Jaguar games match the arcade look):
// scanline beams that widen with brightness, slot mask, curved glass, rounded tube corners, misconvergence,
// halation and glow, rolling hum bar, noise and vignette.
uniform vec4 sSrcSize;     // Jaguar picture size (w, h, 1/w, 1/h)
uniform vec4 sRTSize;      // output size in pixels
uniform float sTime;
uniform float sCurve;
uniform float sScan;
uniform float sMask;
uniform float sHalation;
uniform float sGlow;
uniform float sBright;
uniform float sLines;

layout(binding = 0) uniform sampler2D sLin;   // linear light picture
layout(binding = 1) uniform sampler2D sBloomA; // half-res blur
layout(binding = 2) uniform sampler2D sBloomB; // eighth-res blur

layout(location = 0) in vec2 ilUV;
layout(location = 0) out vec4 outColor0;

float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }

vec2 warpUV(vec2 t)
{
	vec2 c = t * 2.0 - 1.0;
	c *= vec2(1.0 + c.y * c.y * sCurve, 1.0 + c.x * c.x * sCurve * 1.33);
	return c * 0.5 + 0.5;
}

float fetch(vec2 p, int ch)
{
	float x = p.x * sSrcSize.x - 0.5;
	float xi = floor(x);
	float f = x - xi;
	f = clamp((f - 0.5) * 1.8 + 0.5, 0.0, 1.0);
	return texture(sLin, vec2((xi + 0.5 + f) * sSrcSize.z, p.y))[ch];
}

vec3 beam(vec2 p, float lines)
{
	float y = p.y * lines - 0.5;
	float y0 = floor(y);
	float fy = y - y0;
	float conv = 0.35;
	vec3 sum = vec3(0.0);
	vec3 plain = vec3(0.0);
	for (int i = -1; i <= 2; i++)
	{
		float li = y0 + float(i);
		float v = (li + 0.5) / lines;
		if (v < 0.0 || v > 1.0) continue;
		float d = abs(fy - float(i));
		vec3 c = vec3(fetch(vec2(p.x + conv * sSrcSize.z, v), 0),
		              fetch(vec2(p.x, v), 1),
		              fetch(vec2(p.x - conv * sSrcSize.z, v), 2));
		vec3 w = mix(vec3(0.24), vec3(0.52), pow(c, vec3(0.6)));
		vec3 wt = exp(-0.5 * d * d / (w * w)) / (w * 2.5066);
		sum += c * wt;
		if (i == 0) plain += c * (1.0 - fy);
		if (i == 1) plain += c * fy;
	}
	return mix(plain, sum, sScan);
}

vec3 slotMask(vec2 fc, float pitch)
{
	float px = floor(fc.x / pitch);
	float py = floor(fc.y / pitch);
	float col = mod(px, 3.0);
	float triad = floor(px / 3.0);
	vec3 m = vec3(col < 0.5 ? 1.0 : 0.0, (col > 0.5 && col < 1.5) ? 1.0 : 0.0, col > 1.5 ? 1.0 : 0.0);
	float row = mod(py + mod(triad, 2.0) * 2.0, 4.0);
	if (row > 2.5) m *= 0.35;
	vec3 dark = vec3(1.0 - sMask);
	return mix(dark, vec3(1.0), m) / (1.0 - sMask * 0.62);
}

float roundRect(vec2 p, float aspect, float r)
{
	vec2 q = abs((p - 0.5) * vec2(aspect, 1.0)) - (vec2(0.5 * aspect, 0.5) - r);
	return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

void main()
{
	vec2 t = ilUV;
	vec2 fc = t * sRTSize.xy;
	float aspect = sRTSize.x / sRTSize.y;
	vec2 p = warpUV(t);
	float lines = sLines > 0.5 ? sLines : sSrcSize.y;
	float dTube = roundRect(p, aspect, 0.035);
	vec3 col = vec3(0.0);
	if (dTube < 0.0 && all(greaterThanEqual(p, vec2(0.0))) && all(lessThanEqual(p, vec2(1.0))))
	{
		col = beam(p, lines);
		col *= slotMask(fc, max(1.0, floor(sRTSize.y / 720.0 + 0.5)));
		col += texture(sBloomA, p).rgb * sHalation;
		col += texture(sBloomB, p).rgb * sGlow;
		col *= 1.0 - 0.018 * (0.5 + 0.5 * sin((p.y - sTime * 0.11) * 6.2832));
		col *= sBright;
		col += vec3(0.010, 0.012, 0.011);
		float vig = 16.0 * p.x * p.y * (1.0 - p.x) * (1.0 - p.y);
		col *= mix(0.55, 1.0, pow(clamp(vig, 0.0, 1.0), 0.25));
		float g = smoothstep(0.55, 0.0, length((t - vec2(0.22, 0.16)) * vec2(aspect, 1.0)));
		col += vec3(0.020, 0.022, 0.026) * g;
		col += (hash(fc * 0.731 + fract(sTime * 7.13)) - 0.5) * 0.01;
		col *= 1.0 - smoothstep(-0.004, 0.0, dTube);
	}
	col = max(col, 0.0);
	col = pow(col, vec3(1.0 / 2.2));
	col += (hash(fc + 0.37) - 0.5) / 255.0;
	outColor0 = vec4(col, 1.0);
}
