using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using System.Collections.Generic;

/// <summary>
/// Ekranda sadece belirttiðiniz UI kökü (root) üzerinde baþlayan týklama/dokunmalarý oyun tarafýnda "iptal" eder.
/// Farklý yazým stiliyle, ayný iþlevi saðlar.
/// Attach edin ve inspector'dan "targetUIRoot" olarak bloklanmasýný istediðiniz RectTransform'u verin.
/// </summary>
[DefaultExecutionOrder(-9999)] // Erken çalýþsýn, ama farklý bir deðer kullandýk.
public class SpecificUIBlocker : MonoBehaviour
{
    [Tooltip("Bu RectTransform ve çocuklarý üzerindeki týklamalarý bloklar. (Örn: panel kökü)")]
    public RectTransform targetUIRoot;

    [Tooltip("Eðer true ise, sadece raycast sonuçlarýnýn en üstündeki obje target'in çocuðuyken bloklar. False ise sonuçlarýn herhangi birinde target bulunursa bloklar.")]
    public bool requireTopmostHit = false;

    [Tooltip("Ýsteðe baðlý: manuel olarak bir GraphicRaycaster atayabilirsiniz. Boþ ise, target'in parent Canvas'ýndan alýnýr.")]
    public GraphicRaycaster graphicRaycaster;

    /// <summary>
    /// Diðer sistemlerin bu frame bloklandý mý kontrol etmesi için okunabilir.
    /// </summary>
    public static bool WasBlockedThisFrame { get; private set; }

    PointerEventData tempPointer;
    List<RaycastResult> raycastResults = new List<RaycastResult>();

    void Awake()
    {
        tempPointer = new PointerEventData(EventSystem.current);
        if (graphicRaycaster == null && targetUIRoot != null)
        {
            var c = targetUIRoot.GetComponentInParent<Canvas>();
            if (c != null) graphicRaycaster = c.GetComponent<GraphicRaycaster>();
        }
    }

    void Update()
    {
        // Reset flag every frame
        WasBlockedThisFrame = false;

        if (EventSystem.current == null) return;

        // Fare týklamalarý (sol/orta/sað)
        if (Input.GetMouseButtonDown(0) || Input.GetMouseButtonDown(1) || Input.GetMouseButtonDown(2))
        {
            if (IsPointerOverTargetUI(Input.mousePosition))
            {
                Input.ResetInputAxes();
                WasBlockedThisFrame = true;
                return;
            }
        }

        // Dokunuþlar (mobil)
        int tc = Input.touchCount;
        for (int i = 0; i < tc; i++)
        {
            Touch t = Input.GetTouch(i);
            if (t.phase == TouchPhase.Began)
            {
                if (IsPointerOverTargetUI(t.position))
                {
                    Input.ResetInputAxes();
                    WasBlockedThisFrame = true;
                    return;
                }
            }
        }
    }

    bool IsPointerOverTargetUI(Vector2 screenPosition)
    {
        // Eðer target yok, fallback: tüm UI'larý kontrol et (eski davranýþa benzer)
        if (targetUIRoot == null)
        {
            // Basit global UI kontrolü:
            return EventSystem.current.IsPointerOverGameObject();
        }

        // Hazýr bir GraphicRaycaster yoksa dene tekrar bulmayý
        if (graphicRaycaster == null)
        {
            var c = targetUIRoot.GetComponentInParent<Canvas>();
            if (c != null) graphicRaycaster = c.GetComponent<GraphicRaycaster>();
            if (graphicRaycaster == null)
            {
                // hiçbir raycaster yoksa, yine fallback: EventSystem check
                return EventSystem.current.IsPointerOverGameObject();
            }
        }

        // Raycast için PointerEventData hazýrla
        tempPointer.position = screenPosition;
        tempPointer.pointerId = -1; // mouse için -1, touch için fingerId atanmýyor burada (konum yeterli)

        raycastResults.Clear();
        graphicRaycaster.Raycast(tempPointer, raycastResults);

        if (raycastResults == null || raycastResults.Count == 0) return false;

        if (requireTopmostHit)
        {
            // Sadece en üstteki sonuç target'in çocuðuysa block et
            var top = raycastResults[0];
            if (top.gameObject != null && IsTransformChildOf(top.gameObject.transform, targetUIRoot))
                return true;
            return false;
        }
        else
        {
            // Sonuçlarýn herhangi birinde target'in çocuðu varsa block et
            for (int i = 0; i < raycastResults.Count; i++)
            {
                var go = raycastResults[i].gameObject;
                if (go != null && IsTransformChildOf(go.transform, targetUIRoot))
                    return true;
            }
            return false;
        }
    }

    static bool IsTransformChildOf(Transform child, Transform parent)
    {
        if (child == null || parent == null) return false;
        if (child == parent) return true;
        while (child.parent != null)
        {
            if (child.parent == parent) return true;
            child = child.parent;
        }
        return false;
    }
}