// 콘텐츠를 제자리에 둔 채 알파만 서서히 드러내는 UI 셰이더 (등장 연출 → ArUcoAppear).
// Resources 에 두어 빌드에서 스트립되지 않게 한다. ArUcoProjectionView 가 Resources.Load 로 연다.
//
// UI/Default 를 줄인 것에 _Reveal 마스크만 얹었다. 마스크는 텍스처 UV 로 계산하므로
// 마커 평면 위에 눕혀 워프된 상태에서도 콘텐츠 기준으로 "위에서부터", "가운데서 나선으로" 가 유지된다.
//
// _Mode  0 = 마스크 없음   1 = 위에서부터(v=1 → 0)   2 = 가운데서 나선으로
// _Reveal 0 → 1 로 올리면 드러난다. _Soft 는 경계의 부드러운 폭(마스크 값 단위).
//
// 주의: ArUcoWarpedImage.FlipV 로 UV 를 뒤집은 영상(AVPro, Windows)에서는 v 방향이 반대라
// 실제 파이프라인에 옮길 때 _Flip 을 1 로 준다.
Shader "StoryRest/ArUcoReveal"
{
    Properties
    {
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Reveal ("Reveal", Range(0, 1)) = 1
        _Mode ("Mode (0 none, 1 top, 2 spiral)", Float) = 0
        _Soft ("Soft edge", Float) = 0.12
        _Aspect ("Aspect (h/w)", Float) = 0.5625
        _Turns ("Spiral turns", Float) = 2
        _Flip ("Flip V", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "RenderType" = "Transparent"
            "PreviewType" = "Plane"
            "CanUseSpriteAtlas" = "True"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata_t
            {
                float4 vertex : POSITION;
                float4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                fixed4 color : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            sampler2D _MainTex;
            fixed4 _Color;
            float _Reveal;
            float _Mode;
            float _Soft;
            float _Aspect;
            float _Turns;
            float _Flip;

            v2f vert(appdata_t v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = v.texcoord;
                o.color = v.color * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.texcoord) * i.color;
                if (_Mode < 0.5) return col;

                float2 uv = i.texcoord;
                if (_Flip > 0.5) uv.y = 1.0 - uv.y;

                // 마스크 값 m: 0 이 먼저 드러나고 1 이 마지막에 드러난다.
                float m;
                if (_Mode > 1.5)
                {
                    // 화면 비율을 반영해 실제 원형 나선이 되게 y 를 aspect 로 줄인다.
                    float2 d = float2(uv.x - 0.5, (uv.y - 0.5) * _Aspect);
                    float r = length(d) / (0.5 * sqrt(1.0 + _Aspect * _Aspect));   // 모서리 = 1
                    float a = atan2(d.y, d.x) / 6.2831853 + 0.5;                   // 0 ~ 1
                    // frac 항이 나선 팔을 만들고(감기는 모양), r 항이 전체를 가운데서 바깥으로 밀어낸다.
                    m = frac(a + r * _Turns) * 0.45 + r * 0.55;
                }
                else
                {
                    m = 1.0 - uv.y;   // 위(v = 1)가 0
                }

                float edge = _Reveal * (1.0 + _Soft);
                col.a *= 1.0 - smoothstep(edge - _Soft, edge, m);
                return col;
            }
            ENDCG
        }
    }
}
