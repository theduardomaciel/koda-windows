// SmoothMotion.Windows: portable block motion estimation on DirectCompute.
// All motion vectors are measured in the current pyramid level's pixels.
cbuffer Parameters : register(b0) {
    uint2 size;
    uint2 fieldSize;
    uint2 parentSize;
    uint radius;
    uint hasParent;
    float phase;
    float3 padding;
};
Texture2D<float4> a : register(t0);
Texture2D<float4> b : register(t1);
Texture2D<float4> parent : register(t2);
Texture2D<float4> backward : register(t3);
Texture2D<float4> forward : register(t4);
Texture2D<uint> scene : register(t5);
RWTexture2D<float4> destination : register(u0);
RWTexture2D<uint> counts : register(u1);
SamplerState linearClamp : register(s0);

float luma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

[numthreads(8,8,1)]
void Luma(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= size)) return;
    float2 uv = (id.xy + 0.5) / size;
    float value = luma(a.SampleLevel(linearClamp, uv, 0).rgb);
    destination[id.xy] = float4(value, value, value, 1);
}

float cost(int2 centre, int2 displacement) {
    float error = 0;
    // A 12x12 matching window sampled on a 2px grid, centred on an 8px cell.
    [unroll] for (int y=-5; y<=5; y+=2) {
        [unroll] for (int x=-5; x<=5; x+=2) {
            int2 p = clamp(centre + int2(x,y), 0, int2(size)-1);
            int2 q = clamp(p + displacement, 0, int2(size)-1);
            error += abs(a.Load(int3(p,0)).r - b.Load(int3(q,0)).r);
        }
    }
    return error / 36;
}

[numthreads(8,8,1)]
void Search(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= fieldSize)) return;
    int2 centre = min(id.xy * 8 + 4, size - 1);
    int2 prediction = 0;
    if (hasParent != 0) {
        float2 uv = (id.xy + 0.5) / fieldSize;
        // Field textures are small; interpolate predictions between cell centres.
        prediction = int2(round(parent.SampleLevel(linearClamp, uv, 0).xy * 2));
    }
    int2 best = prediction;
    float score = cost(centre, best);
    // Always consider zero: a moving foreground must not pull a static background.
    float zero = cost(centre, 0);
    if (zero < score) { best = 0; score = zero; }
    for (int y=-(int)radius; y<=(int)radius; ++y) {
        for (int x=-(int)radius; x<=(int)radius; ++x) {
            int2 v = prediction + int2(x,y);
            float c = cost(centre, v);
            // Prefer the prediction on near-ties, avoiding noise on flat areas.
            float penalty = 0.0002 * length(float2(v-prediction));
            if (c + penalty < score) { score = c; best = v; }
        }
    }
    destination[id.xy] = float4(best, score, 1);
}

[numthreads(8,8,1)]
void Median(uint3 id : SV_DispatchThreadID) {
    if (any(id.xy >= fieldSize)) return;
    float4 candidates[9];
    int index=0;
    [unroll] for(int y=-1;y<=1;++y)
        [unroll] for(int x=-1;x<=1;++x)
            candidates[index++] = a.Load(int3(clamp(int2(id.xy)+int2(x,y),0,int2(fieldSize)-1),0));
    // A vector median chooses a real neighbour; a component median invents vectors.
    float bestCost=1e20;
    float4 best=candidates[4];
    [unroll] for(int i=0;i<9;++i) {
        float c=0;
        [unroll] for(int j=0;j<9;++j) c += length(candidates[i].xy-candidates[j].xy);
        if(c<bestCost) {bestCost=c;best=candidates[i];}
    }
    // Preserve the matching error of this cell for confidence and cut detection.
    best.z = candidates[4].z;
    destination[id.xy]=best;
}

[numthreads(8,8,1)]
void Scene(uint3 id : SV_DispatchThreadID) {
    if(any(id.xy >= fieldSize)) return;
    if(a.Load(int3(id.xy,0)).z > 0.16) InterlockedAdd(counts[uint2(0,0)], 1);
}

[numthreads(8,8,1)]
void Render(uint3 id : SV_DispatchThreadID) {
    if(any(id.xy >= size)) return;
    float2 uv=(id.xy+0.5)/size;
    // Exact endpoints and cuts never blend unrelated pictures.
    if(phase <= 0 || phase >= 1 || scene.Load(int3(0,0,0)) > fieldSize.x*fieldSize.y*0.55) {
        destination[id.xy] = phase < 0.5 ? a.Load(int3(id.xy,0)) : b.Load(int3(id.xy,0));
        return;
    }
    float4 f=forward.SampleLevel(linearClamp,uv,0);
    float4 r=backward.SampleLevel(linearClamp,uv,0);
    // Fields live on the finest (<=320px) luma image, rather than the full video.
    float2 motionSize=float2(parentSize);
    float2 ua=uv+r.xy*phase/motionSize;
    float2 ub=uv+f.xy*(1-phase)/motionSize;
    float4 wa=a.SampleLevel(linearClamp,ua,0);
    float4 wb=b.SampleLevel(linearClamp,ub,0);
    float4 warped=lerp(wa,wb,phase);
    float4 plain=lerp(a.SampleLevel(linearClamp,uv,0),b.SampleLevel(linearClamp,uv,0),phase);
    float confidence=1-smoothstep(0.025,0.14,max(f.z,r.z));
    // Poor matches soften smoothly, instead of exposing block boundaries.
    destination[id.xy]=lerp(plain,warped,confidence);
}
