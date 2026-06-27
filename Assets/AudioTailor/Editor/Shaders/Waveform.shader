Shader "Hidden/AudioTailor/Waveform"
{
    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            StructuredBuffer<float> _AudioBuffer;
            int _FrameCount;
            int _Channels;
            int _PixelWidth;
            int _PixelHeight;
            float4 _WaveColor;
            float4 _BackgroundColor;

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                int x  = clamp((int)(i.uv.x * _PixelWidth), 0, _PixelWidth - 1);
                int f0 = (int)((float)x       / _PixelWidth * _FrameCount);
                int f1 = min(max(f0 + 1, (int)((float)(x + 1) / _PixelWidth * _FrameCount)), _FrameCount);

                float lo =  1e10, hi = -1e10;
                for (int f = f0; f < f1; f++)
                {
                    float v = _AudioBuffer[f * _Channels];
                    lo = min(lo, v);
                    hi = max(hi, v);
                }

                // Map UV y [0,1] → signal range [-1,1]
                float y   = i.uv.y * 2.0 - 1.0;
                float d   = abs(y - clamp(y, lo, hi));

                // 1-pixel soft edge: scale by height (1 pixel = 2.0/_PixelHeight in signal space)
                float val = 1.0 - saturate(d * _PixelHeight * 0.5);
                return lerp(_BackgroundColor, _WaveColor, sqrt(val));
            }
            ENDCG
        }
    }
}
