// Arcade launcher CRT for Flycast (Dreamcast / Naomi), run through ReShade.
// A port of the launcher menu's CRT shader (the same one BigPEmu uses for Jaguar games), so every system on the
// cabinet has the same tube: scanline beams that widen with brightness, slot mask, curved glass, rounded corners,
// misconvergence, halation and glow, a faint rolling hum bar, noise and vignette.
//
// Flycast draws its 4:3 picture in the middle of the screen. This shader reads that picture back at the Dreamcast's
// 640x480 and redraws it as a tube; set the game to render at 480 lines (config\games.json "flycast") for sprites.

uniform float sCurve    < ui_type = "slider"; ui_label = "Curvature";   ui_min = 0.0; ui_max = 0.3; ui_step = 0.01; > = 0.06;
uniform float sScan     < ui_type = "slider"; ui_label = "Scanlines";   ui_min = 0.0; ui_max = 1.0; ui_step = 0.05; > = 1.0;
uniform float sMask     < ui_type = "slider"; ui_label = "Slot mask";   ui_min = 0.0; ui_max = 0.8; ui_step = 0.05; > = 0.35;
uniform float sHalation < ui_type = "slider"; ui_label = "Halation";    ui_min = 0.0; ui_max = 1.0; ui_step = 0.02; > = 0.18;
uniform float sGlow     < ui_type = "slider"; ui_label = "Glow";        ui_min = 0.0; ui_max = 1.0; ui_step = 0.02; > = 0.22;
uniform float sBright   < ui_type = "slider"; ui_label = "Brightness";  ui_min = 0.5; ui_max = 2.5; ui_step = 0.05; > = 1.35;
uniform float sTimer    < source = "timer"; >;

#define SRC_W 640
#define SRC_H 480
#define PI2 6.2831853

texture BackBufferTex : COLOR;
sampler BackBuffer { Texture = BackBufferTex; MinFilter = LINEAR; MagFilter = LINEAR; AddressU = CLAMP; AddressV = CLAMP; };

texture LinTex     { Width = SRC_W;     Height = SRC_H;     Format = RGBA16F; };
texture BloomHTex  { Width = SRC_W / 2; Height = SRC_H / 2; Format = RGBA16F; };
texture BloomATex  { Width = SRC_W / 2; Height = SRC_H / 2; Format = RGBA16F; };
texture BloomBHTex { Width = SRC_W / 8; Height = SRC_H / 8; Format = RGBA16F; };
texture BloomBTex  { Width = SRC_W / 8; Height = SRC_H / 8; Format = RGBA16F; };
sampler Lin     { Texture = LinTex;     MinFilter = LINEAR; MagFilter = LINEAR; AddressU = CLAMP; AddressV = CLAMP; };
sampler BloomH  { Texture = BloomHTex;  MinFilter = LINEAR; MagFilter = LINEAR; AddressU = CLAMP; AddressV = CLAMP; };
sampler BloomA  { Texture = BloomATex;  MinFilter = LINEAR; MagFilter = LINEAR; AddressU = CLAMP; AddressV = CLAMP; };
sampler BloomBH { Texture = BloomBHTex; MinFilter = LINEAR; MagFilter = LINEAR; AddressU = CLAMP; AddressV = CLAMP; };
sampler BloomB  { Texture = BloomBTex;  MinFilter = LINEAR; MagFilter = LINEAR; AddressU = CLAMP; AddressV = CLAMP; };

void FullscreenVS(in uint id : SV_VertexID, out float4 pos : SV_Position, out float2 uv : TEXCOORD)
{
	uv.x = (id == 2) ? 2.0 : 0.0;
	uv.y = (id == 1) ? 2.0 : 0.0;
	pos = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
}

// Where Flycast put the 4:3 picture on screen (pillarboxed on a 16:9 display).
float2 PicSize() { float h = BUFFER_HEIGHT; return float2(min(BUFFER_WIDTH, h * 4.0 / 3.0), h); }
float2 PicOrigin() { return (float2(BUFFER_WIDTH, BUFFER_HEIGHT) - PicSize()) * 0.5; }

float hash(float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }

// Pass 1: the picture at the Dreamcast's resolution, in linear light.
float4 LinPS(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target
{
	float2 px = PicOrigin() + uv * PicSize();
	float3 c = tex2D(BackBuffer, px / float2(BUFFER_WIDTH, BUFFER_HEIGHT)).rgb;
	return float4(pow(abs(c), 2.2), 1.0);
}

float3 Blur(sampler s, float2 uv, float2 stepv)
{
	static const float o[5] = { 0.0, 1.3846153846, 3.2307692308, 5.0769230769, 6.9230769231 };
	static const float w[5] = { 0.2270270270, 0.3162162162, 0.0702702703, 0.0108108108, 0.0010810811 };
	float3 sum = tex2D(s, uv).rgb * w[0];
	[unroll] for (int i = 1; i < 5; i++)
	{
		sum += tex2D(s, uv + stepv * o[i] * 1.6).rgb * w[i];
		sum += tex2D(s, uv - stepv * o[i] * 1.6).rgb * w[i];
	}
	return sum;
}
float4 BlurAH(float4 p : SV_Position, float2 uv : TEXCOORD) : SV_Target { return float4(Blur(Lin,     uv, float2(2.0 / SRC_W, 0.0)), 1.0); }
float4 BlurAV(float4 p : SV_Position, float2 uv : TEXCOORD) : SV_Target { return float4(Blur(BloomH,  uv, float2(0.0, 2.0 / SRC_H)), 1.0); }
float4 BlurBH(float4 p : SV_Position, float2 uv : TEXCOORD) : SV_Target { return float4(Blur(BloomA,  uv, float2(8.0 / SRC_W, 0.0)), 1.0); }
float4 BlurBV(float4 p : SV_Position, float2 uv : TEXCOORD) : SV_Target { return float4(Blur(BloomBH, uv, float2(0.0, 8.0 / SRC_H)), 1.0); }

float2 warpUV(float2 t)
{
	float2 c = t * 2.0 - 1.0;
	c *= float2(1.0 + c.y * c.y * sCurve, 1.0 + c.x * c.x * sCurve * 1.33);
	return c * 0.5 + 0.5;
}

// Horizontal sample with sharpened pixel edges (as the menu's shader)
float3 fetch3(float2 p, float conv)
{
	float3 r;
	[unroll] for (int ch = 0; ch < 3; ch++)
	{
		float x = (p.x + (1 - ch) * conv / SRC_W) * SRC_W - 0.5;
		float xi = floor(x);
		float f = saturate((x - xi - 0.5) * 1.8 + 0.5);
		r[ch] = tex2Dlod(Lin, float4((xi + 0.5 + f) / SRC_W, p.y, 0, 0))[ch];
	}
	return r;
}

float3 beam(float2 p)
{
	float lines = SRC_H;
	float y = p.y * lines - 0.5;
	float y0 = floor(y);
	float fy = y - y0;
	float3 sum = 0.0, plain = 0.0;
	[unroll] for (int i = -1; i <= 2; i++)
	{
		float v = (y0 + i + 0.5) / lines;
		float3 c = (v < 0.0 || v > 1.0) ? 0.0 : fetch3(float2(p.x, v), 0.35);
		float d = abs(fy - i);
		float3 w = lerp(0.24, 0.52, pow(abs(c), 0.6));
		sum += c * exp(-0.5 * d * d / (w * w)) / (w * 2.5066);
		if (i == 0) plain += c * (1.0 - fy);
		if (i == 1) plain += c * fy;
	}
	return lerp(plain, sum, sScan);
}

float3 slotMask(float2 fc, float pitch)
{
	float px = floor(fc.x / pitch), py = floor(fc.y / pitch);
	float col = px % 3.0;
	float triad = floor(px / 3.0);
	float3 m = float3(col < 0.5 ? 1.0 : 0.0, (col > 0.5 && col < 1.5) ? 1.0 : 0.0, col > 1.5 ? 1.0 : 0.0);
	if ((py + (triad % 2.0) * 2.0) % 4.0 > 2.5) m *= 0.35;
	return lerp(1.0 - sMask, 1.0, m) / (1.0 - sMask * 0.62);
}

float roundRect(float2 p, float aspect, float r)
{
	float2 q = abs((p - 0.5) * float2(aspect, 1.0)) - (float2(0.5 * aspect, 0.5) - r);
	return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

float4 CrtPS(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target
{
	float2 fc = pos.xy;
	float2 t = (fc - PicOrigin()) / PicSize();   // 0..1 across the 4:3 picture
	float aspect = PicSize().x / PicSize().y;
	float time = sTimer * 0.001;
	float2 p = warpUV(t);
	float dTube = roundRect(p, aspect, 0.035);
	float3 col = 0.0;
	if (dTube < 0.0 && all(p >= 0.0) && all(p <= 1.0))
	{
		col = beam(p);
		col *= slotMask(fc, max(1.0, floor(BUFFER_HEIGHT / 720.0 + 0.5)));
		col += tex2D(BloomA, p).rgb * sHalation;
		col += tex2D(BloomB, p).rgb * sGlow;
		col *= 1.0 - 0.018 * (0.5 + 0.5 * sin((p.y - time * 0.11) * PI2));
		col *= sBright;
		col += float3(0.010, 0.012, 0.011);
		float vig = 16.0 * p.x * p.y * (1.0 - p.x) * (1.0 - p.y);
		col *= lerp(0.55, 1.0, pow(saturate(vig), 0.25));
		float g = smoothstep(0.55, 0.0, length((t - float2(0.22, 0.16)) * float2(aspect, 1.0)));
		col += float3(0.020, 0.022, 0.026) * g;
		col += (hash(fc * 0.731 + frac(time * 7.13)) - 0.5) * 0.01;
		col *= 1.0 - smoothstep(-0.004, 0.0, dTube);
	}
	col = pow(max(col, 0.0), 1.0 / 2.2);
	col += (hash(fc + 0.37) - 0.5) / 255.0;
	return float4(col, 1.0);
}

technique ArcadeCRT < ui_label = "Arcade CRT (launcher)"; >
{
	pass Lin    { VertexShader = FullscreenVS; PixelShader = LinPS;  RenderTarget = LinTex; }
	pass AH     { VertexShader = FullscreenVS; PixelShader = BlurAH; RenderTarget = BloomHTex; }
	pass AV     { VertexShader = FullscreenVS; PixelShader = BlurAV; RenderTarget = BloomATex; }
	pass BH     { VertexShader = FullscreenVS; PixelShader = BlurBH; RenderTarget = BloomBHTex; }
	pass BV     { VertexShader = FullscreenVS; PixelShader = BlurBV; RenderTarget = BloomBTex; }
	pass Final  { VertexShader = FullscreenVS; PixelShader = CrtPS; }
}
