using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace ClarryStage
{
    /// <summary>
    /// 把舞台画出来。它只做两件事：把 /api/cast 的每一个人摆成一张卡，把 /api/history 的
    /// 最后十句排成一条台词流。
    ///
    /// 有一件事它刻意不做：决定这一格用哪张画。服务端给的是候选链（这一格专用的、线稿、
    /// 像素表情、常态），客户端只按顺序试，第一张能加载的就是这一格该用的。它不自己算，
    /// 因为分镜逻辑各写一份之后，每改一次镜头就要改两处。
    ///
    /// 卡片的布局用锚点，不用 VerticalLayoutGroup。理由是试过两版布局组：关掉
    /// childControlHeight，立绘退回 100×100 的默认框，被 preserveAspect 压成拇指；
    /// 打开它，立绘拿走了全部高度，两行字一个字都看不见。锚点没有这类歧义：
    /// 卡片多高，立绘就是「多高减去底下 74 像素」，剩下的位置本来就是留给字的。
    /// </summary>
    public class StageView : MonoBehaviour
    {
        const float PollSeconds = 1.5f;
        const int LogLines = 10;

        readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();
        readonly Dictionary<string, Card> _cards = new Dictionary<string, Card>();

        Font _font;
        RectTransform _row;
        Text _log;

        class Card
        {
            public GameObject Root;
            public Image Portrait;
            public Text Name;
            public Text Line;
            public string Want;
        }

        void Start()
        {
            // Unity 内置字体没有汉字。从系统里取一款：这是唯一能让中文显示成字而不是方块的
            // 办法，而一个满屏方块的名字比不显示还糟。
            _font = Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft YaHei", "SimHei", "Noto Sans CJK SC", "Arial" }, 20);

            var canvas = gameObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            var scaler = gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1600f, 900f);
            scaler.matchWidthOrHeight = 0.5f;

            gameObject.AddComponent<GraphicRaycaster>();

            BuildBackground();
            BuildRow();
            BuildLog();

            StartCoroutine(Poll());
        }

        void BuildBackground()
        {
            var go = new GameObject("底", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(transform, false);
            var img = go.GetComponent<Image>();
            img.color = new Color(0.07f, 0.08f, 0.10f, 1f);
            img.raycastTarget = false;
            Stretch(go.GetComponent<RectTransform>());
        }

        void BuildRow()
        {
            var go = new GameObject("一行", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            _row = go.GetComponent<RectTransform>();
            _row.anchorMin = new Vector2(0f, 0.36f);
            _row.anchorMax = new Vector2(1f, 1f);
            _row.offsetMin = new Vector2(20f, 10f);
            _row.offsetMax = new Vector2(-20f, -10f);

            var layout = go.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 14f;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
        }

        void BuildLog()
        {
            var go = new GameObject("台词", typeof(RectTransform));
            go.transform.SetParent(transform, false);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = new Vector2(1f, 0.36f);
            rt.offsetMin = new Vector2(28f, 18f);
            rt.offsetMax = new Vector2(-28f, -6f);

            _log = go.AddComponent<Text>();
            _log.font = _font;
            _log.fontSize = 17;
            _log.color = new Color(0.88f, 0.89f, 0.92f);
            _log.alignment = TextAnchor.LowerLeft;
            _log.horizontalOverflow = HorizontalWrapMode.Wrap;
            _log.verticalOverflow = VerticalWrapMode.Truncate;
            _log.raycastTarget = false;
        }

        IEnumerator Poll()
        {
            while (true)
            {
                yield return ClarryApi.GetCast(OnCast);
                yield return ClarryApi.GetHistory(LogLines, OnHistory);
                yield return new WaitForSeconds(PollSeconds);
            }
        }

        void OnCast(ClarryApi.CastMember[] members)
        {
            foreach (var m in members)
            {
                if (!_cards.TryGetValue(m.id, out var card))
                {
                    card = BuildCard();
                    _cards[m.id] = card;
                }
                else
                {
                    card.Root.transform.SetAsLastSibling();
                }

                card.Name.text = m.name;
                card.Line.text = m.role ?? "";

                // 拿候选链的第一项当「这一格想要什么」的指纹：它变了就说明表情或镜头变了，
                // 该重新挑图；没变就别重复下载。
                var want = m.faces != null && m.faces.Length > 0 ? m.faces[0] : m.face;
                if (card.Want != want)
                {
                    card.Want = want;
                    StartCoroutine(LoadPortrait(card, m.faces, m.face));
                }
            }

            // 不在台上的人收起来，而不是删掉：他可能下一拍又回来，而重建一张卡要重新下一张图。
            foreach (var kv in _cards)
            {
                var on = System.Array.Exists(members, x => x.id == kv.Key);
                if (kv.Value.Root.activeSelf != on) kv.Value.Root.SetActive(on);
            }
        }

        Card BuildCard()
        {
            var root = new GameObject("卡", typeof(RectTransform), typeof(Image));
            root.transform.SetParent(_row, false);

            var bg = root.GetComponent<Image>();
            bg.color = new Color(0.11f, 0.12f, 0.15f, 1f);
            bg.raycastTarget = false;

            // 立绘撑满卡片上方，底下留 74 像素给两行字。框比图「宽」的时候
            // preserveAspect 会让图按高度缩并居中，所以不管立绘是什么比例都不会变形。
            var portraitGo = new GameObject("立绘", typeof(RectTransform), typeof(Image));
            portraitGo.transform.SetParent(root.transform, false);
            var portrait = portraitGo.GetComponent<Image>();
            portrait.preserveAspect = true;
            portrait.raycastTarget = false;
            // 还没有图的时候不要露一个白方块：Image 没有 sprite 会画成纯白，
            // 而一张白板看起来像「这个人坏了」，不像「图还没到」。
            portrait.enabled = false;
            var pr = portraitGo.GetComponent<RectTransform>();
            pr.anchorMin = new Vector2(0f, 0f);
            pr.anchorMax = new Vector2(1f, 1f);
            pr.offsetMin = new Vector2(8f, 74f);
            pr.offsetMax = new Vector2(-8f, -8f);

            var nameGo = new GameObject("名字", typeof(RectTransform));
            nameGo.transform.SetParent(root.transform, false);
            var nameText = nameGo.AddComponent<Text>();
            nameText.font = _font;
            nameText.fontSize = 20;
            nameText.color = Color.white;
            nameText.alignment = TextAnchor.MiddleCenter;
            nameText.horizontalOverflow = HorizontalWrapMode.Overflow;
            nameText.raycastTarget = false;
            var nr = nameGo.GetComponent<RectTransform>();
            nr.anchorMin = new Vector2(0f, 0f);
            nr.anchorMax = new Vector2(1f, 0f);
            nr.offsetMin = new Vector2(8f, 44f);
            nr.offsetMax = new Vector2(-8f, 70f);

            var lineGo = new GameObject("身份", typeof(RectTransform));
            lineGo.transform.SetParent(root.transform, false);
            var lineText = lineGo.AddComponent<Text>();
            lineText.font = _font;
            lineText.fontSize = 15;
            lineText.color = new Color(0.72f, 0.74f, 0.80f);
            lineText.alignment = TextAnchor.UpperCenter;
            lineText.horizontalOverflow = HorizontalWrapMode.Wrap;
            lineText.verticalOverflow = VerticalWrapMode.Truncate;
            lineText.raycastTarget = false;
            var lr = lineGo.GetComponent<RectTransform>();
            lr.anchorMin = new Vector2(0f, 0f);
            lr.anchorMax = new Vector2(1f, 0f);
            lr.offsetMin = new Vector2(8f, 2f);
            lr.offsetMax = new Vector2(-8f, 42f);

            return new Card { Root = root, Portrait = portrait, Name = nameText, Line = lineText };
        }

        /// <summary>
        /// 按服务端给的候选链逐张试，第一张能加载的就是这一格该用的。
        ///
        /// 客户端不自己算用哪张，理由就是这个列表：它是从最具体排到一定存在的，一个没有
        /// 画表情差分的人自然落到常态立绘上。早一版只读列表第一项，于是每一个没画的表情
        /// 都变成一个 404 和一张白卡。
        /// </summary>
        IEnumerator LoadPortrait(Card card, string[] candidates, string fallback)
        {
            card.Portrait.enabled = false;

            var list = candidates != null && candidates.Length > 0
                ? candidates
                : (string.IsNullOrEmpty(fallback) ? new string[0] : new[] { fallback });

            if (list.Length == 0) yield break;

            // 已经下过的那几张先过一遍，不重复走网络。
            foreach (var path in list)
            {
                if (!_sprites.TryGetValue(path, out var cached)) continue;
                card.Portrait.sprite = cached;
                card.Portrait.enabled = true;
                yield break;
            }

            foreach (var path in list)
            {
                Texture2D tex = null;
                yield return ClarryApi.GetPortrait(path, t => tex = t);
                if (tex == null) continue;

                var sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0f), 100f);
                _sprites[path] = sprite;
                card.Portrait.sprite = sprite;
                card.Portrait.enabled = true;
                yield break;
            }

            Debug.LogWarning("[Clarry] " + card.Name.text + " 这一格没有能加载的图，试过：" + string.Join("、", list));
        }

        void OnHistory(ClarryApi.Turn[] turns)
        {
            var lines = new List<string>();
            for (var i = turns.Length - 1; i >= 0 && lines.Count < LogLines; i--)
            {
                var t = turns[i];
                if (string.IsNullOrEmpty(t.text)) continue;

                var who = t.actorId == "世界" ? "　　" : t.actorId + "：";
                var to = string.IsNullOrEmpty(t.addressedTo) ? "" : "→" + t.addressedTo + " ";
                lines.Add(who + to + t.text);
            }

            lines.Reverse();

            var sb = new StringBuilder();
            foreach (var line in lines) sb.AppendLine(line);
            _log.text = sb.ToString();
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
