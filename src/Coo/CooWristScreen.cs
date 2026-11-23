using UnityEngine;
using UnityEngine.UI;

namespace CloudpunkVR.Coo;

// The wrist screen: 可缇 centred at the top, her name under her, the line below, on a small upright screen standing
// on the back of the left forearm.
//
// The screen's contents are a canvas of their own drawn by an offscreen camera into a texture during Update; on the
// arm the texture is a plain quad. A quad is placed in the render callback with the hand's freshest pose and drawn
// where it was put, so it stays fixed to the arm while walking and turning (the earlier world-space canvas trailed
// the arm by a frame when moving).
internal sealed class CooWristScreen
{
    public const float CanvasW = 400, CanvasH = 520;
    public GameObject CanvasRoot;
    public CanvasGroup Group;
    public RawImage Portrait;
    public Text Name, Line;
    Camera cam;
    RenderTexture rt;
    GameObject quad;
    Material quadMat;

    // an unnamed layer no camera draws (the main and cockpit cameras draw 31)
    static int FreeLayer()
    {
        int used = 0;
        foreach (var c in Camera.allCameras) used |= c.cullingMask;
        for (int i = 30; i >= 8; i--) if (string.IsNullOrEmpty(LayerMask.LayerToName(i)) && (used & (1 << i)) == 0) return i;
        for (int i = 30; i >= 8; i--) if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
        return 30;
    }

    static T Child<T>(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size, int layer) where T : Component
    {
        var go = new GameObject(name);
        go.layer = layer;
        go.transform.SetParent(parent, false);
        var r = go.AddComponent<RectTransform>();
        r.anchorMin = anchorMin;
        r.anchorMax = anchorMax;
        r.pivot = new Vector2(0.5f, 1f);
        r.anchoredPosition = pos;
        r.sizeDelta = size;
        return go.AddComponent<T>();
    }

    public void Build(Font font, Color nameColor, Material holo)
    {
        int layer = FreeLayer();
        // far below the city, seen only by its own camera
        var camGo = new GameObject("CloudpunkVR_WristCamera");
        Object.DontDestroyOnLoad(camGo);
        camGo.transform.position = new Vector3(0, -20000, 0);
        cam = camGo.AddComponent<Camera>();
        cam.enabled = false;
        cam.stereoTargetEye = StereoTargetEyeMask.None;
        // framing the canvas exactly: one canvas unit a pixel of the 400x520 layout, the camera 10 in front of it
        camGo.transform.position = new Vector3(0, -20000, -10);
        cam.orthographic = true;
        cam.orthographicSize = CanvasH / 2;
        cam.nearClipPlane = 1;
        cam.farClipPlane = 50;
        cam.cullingMask = 1 << layer;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0, 0, 0, 0);
        cam.allowHDR = false;
        cam.allowMSAA = false;
        cam.useOcclusionCulling = false;
        rt = new RenderTexture((int)CanvasW * 2, (int)CanvasH * 2, 16, RenderTextureFormat.ARGB32) { name = "CloudpunkVR_Wrist", antiAliasing = 1, hideFlags = HideFlags.DontUnloadUnusedAsset };
        rt.Create();
        cam.targetTexture = rt;

        CanvasRoot = new GameObject("CloudpunkVR_CortiWrist");
        Object.DontDestroyOnLoad(CanvasRoot);
        CanvasRoot.layer = layer;
        // a world-space canvas of a fixed size (a screen-space canvas would scale with the game window, not the texture)
        var canvas = CanvasRoot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = cam;
        CanvasRoot.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 2;
        var crt = CanvasRoot.GetComponent<RectTransform>();
        crt.sizeDelta = new Vector2(CanvasW, CanvasH);
        crt.pivot = new Vector2(0.5f, 0.5f);
        CanvasRoot.transform.position = new Vector3(0, -20000, 0);
        CanvasRoot.transform.rotation = Quaternion.identity;
        CanvasRoot.transform.localScale = Vector3.one;
        Group = CanvasRoot.AddComponent<CanvasGroup>();
        var root = CanvasRoot.transform;

        // a cyan frame, the dark glass inside it
        var frame = Child<Image>(root, "Frame", Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero, layer);
        frame.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        frame.color = new Color(0.35f, 0.8f, 1f, 0.75f);
        var glass = Child<Image>(root, "Glass", Vector2.zero, Vector2.one, Vector2.zero, new Vector2(-6, -6), layer);
        glass.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        glass.color = new Color(0.02f, 0.05f, 0.08f, 0.88f);
        var top = new Vector2(0.5f, 1f);
        Portrait = Child<RawImage>(root, "Portrait", top, top, new Vector2(0, -14), new Vector2(340, 340), layer);
        Portrait.material = holo;
        Portrait.raycastTarget = false;
        Name = Child<Text>(root, "Name", top, top, new Vector2(0, -360), new Vector2(380, 48), layer);
        Name.font = font;
        Name.fontSize = 36;
        Name.fontStyle = FontStyle.Bold;
        Name.alignment = TextAnchor.MiddleCenter;
        Name.color = nameColor;
        Name.raycastTarget = false;
        var glow = Name.gameObject.AddComponent<Outline>();
        glow.effectColor = new Color(0.15f, 0.55f, 0.95f, 0.55f);
        glow.effectDistance = new Vector2(2.5f, -2.5f);
        Line = Child<Text>(root, "Line", top, top, new Vector2(0, -410), new Vector2(372, 100), layer);
        Line.font = font;
        Line.fontSize = 30;
        Line.alignment = TextAnchor.UpperCenter;
        Line.color = new Color(0.92f, 0.97f, 1f, 1f);
        Line.horizontalOverflow = HorizontalWrapMode.Wrap;
        Line.verticalOverflow = VerticalWrapMode.Truncate;
        Line.resizeTextForBestFit = true;
        Line.resizeTextMaxSize = 30;
        Line.resizeTextMinSize = 18;
        Line.supportRichText = false;
        Line.raycastTarget = false;

        // the screen on the arm
        quad = new GameObject("CloudpunkVR_WristScreen");
        Object.DontDestroyOnLoad(quad);
        var m = new Mesh { name = "CloudpunkVR_WristQuad" };
        m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(0.5f, -0.5f, 0) };
        m.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
        m.colors = new[] { Color.white, Color.white, Color.white, Color.white };
        m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        m.RecalculateBounds();
        quad.AddComponent<MeshFilter>().sharedMesh = m;
        var mr = quad.AddComponent<MeshRenderer>();
        quadMat = new Material(Shader.Find("UI/Default")) { mainTexture = rt, renderQueue = 3000, hideFlags = HideFlags.DontUnloadUnusedAsset };
        mr.sharedMaterial = quadMat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
        quad.SetActive(false);
        CanvasRoot.SetActive(false);
        Plugin.L.LogInfo($"corti: wrist screen ready (offscreen layer {layer}, {rt.width}x{rt.height})");
    }

    // In Update: draw the contents into the texture (only while it shows).
    public void Render(float alpha)
    {
        if (cam == null) return;
        bool on = alpha > 0;
        if (CanvasRoot.activeSelf != on) CanvasRoot.SetActive(on);
        if (!on) return;
        Canvas.ForceUpdateCanvases();
        cam.Render();
    }

    // In the render callback: the quad where the arm is now.
    public void Place(Vector3 pos, Quaternion rot, float width, float alpha)
    {
        if (quad == null) return;
        bool on = alpha > 0;
        if (quad.activeSelf != on) quad.SetActive(on);
        if (!on) return;
        var t = quad.transform;
        t.position = pos;
        t.rotation = rot;
        t.localScale = new Vector3(width, width * CanvasH / CanvasW, 1);
        quadMat.color = new Color(1, 1, 1, Mathf.Clamp01(alpha * 1.2f));
    }

    public void Hide()
    {
        if (quad != null && quad.activeSelf) quad.SetActive(false);
    }
}
