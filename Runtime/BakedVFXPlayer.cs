using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(MeshRenderer))]
[RequireComponent(typeof(MeshFilter))]
public class BakedVFXPlayer : MonoBehaviour
{
    private const float FrameTime = 1.0f / 30.0f;

    [Header("Playback")]
    [SerializeField]
    private bool playOnEnable = true;

    [SerializeField]
    private bool restartOnEnable = true;

    [SerializeField, Min(0.0f)]
    private float playDelay;

    [SerializeField]
    private bool loop;

    [SerializeField]
    private bool hideOnComplete = true;

    [SerializeField, Range(0.0f, 1.0f)]
    private float alpha = 1.0f;

    private MeshRenderer meshRenderer;
    private MaterialPropertyBlock propertyBlock;

    private float startTime;
    private bool playing;
    private bool waitingForDelay;

    private static readonly int AnimationSettingID =
        Shader.PropertyToID("_AnimationSetting");
    private static readonly int AnimationTextureID =
        Shader.PropertyToID("_AnimTex");
    private static readonly int FrameCountID =
        Shader.PropertyToID("_FrameNum");

    public bool IsPlaying => playing;

    public float PlayDelay
    {
        get => playDelay;
        set => playDelay = Mathf.Max(0.0f, value);
    }

    private void Awake()
    {
        CacheComponents();
    }

    private void OnEnable()
    {
        CacheComponents();

        if (playOnEnable)
        {
            if (restartOnEnable || !playing)
                Play();
        }
    }

    private void Update()
    {
        if (!playing)
            return;

        if (waitingForDelay)
        {
            if (Time.time < startTime)
                return;

            waitingForDelay = false;
            UpdatePropertyBlock();
        }

        float elapsed = Mathf.Max(0.0f, Time.time - startTime);
        int frameCount = GetFrameCount();
        float duration = frameCount * FrameTime;

        if (frameCount <= 0)
        {
            CompletePlayback();
            return;
        }

        if (loop && elapsed >= duration)
        {
            int completedLoops = Mathf.FloorToInt(elapsed / duration);
            startTime += completedLoops * duration;
            UpdatePropertyBlock();
        }
        else if (!loop && elapsed >= duration)
        {
            CompletePlayback();
        }
    }

    private void CompletePlayback()
    {
        playing = false;
        waitingForDelay = false;

        if (hideOnComplete && meshRenderer != null)
            meshRenderer.forceRenderingOff = true;
    }

    private void CacheComponents()
    {
        if (meshRenderer == null)
            meshRenderer = GetComponent<MeshRenderer>();

        if (propertyBlock == null)
            propertyBlock = new MaterialPropertyBlock();
    }

    private int GetFrameCount()
    {
        Material material = meshRenderer != null
            ? meshRenderer.sharedMaterial
            : null;
        if (material == null)
            return 0;

        if (material.HasProperty(AnimationTextureID))
        {
            Texture animationTexture = material.GetTexture(AnimationTextureID);
            if (animationTexture != null)
                return animationTexture.width;
        }

        return material.HasProperty(FrameCountID)
            ? Mathf.Max(0, Mathf.RoundToInt(material.GetFloat(FrameCountID)))
            : 0;
    }

    public void Play()
    {
        Play(Time.time);
    }

    public void Play(float unityStartTime)
    {
        CacheComponents();
        if (GetFrameCount() <= 0)
            return;

        meshRenderer.forceRenderingOff = false;
        startTime = unityStartTime + Mathf.Max(0.0f, playDelay);
        playing = true;
        waitingForDelay = startTime > Time.time;

        UpdatePropertyBlock();
    }

    public void Restart()
    {
        Play();
    }

    public void Stop()
    {
        playing = false;
        waitingForDelay = false;
    }

    public void SetAlpha(float value)
    {
        alpha = Mathf.Clamp01(value);

        if (playing)
            UpdatePropertyBlock();
    }

    private void UpdatePropertyBlock()
    {
        meshRenderer.GetPropertyBlock(propertyBlock);

        // x = animation start time
        // y = instance alpha; keep the effect invisible during delay
        propertyBlock.SetVector(
            AnimationSettingID,
            new Vector4(
                startTime,
                waitingForDelay ? 0.0f : alpha,
                0,
                0));

        meshRenderer.SetPropertyBlock(propertyBlock);
    }
}