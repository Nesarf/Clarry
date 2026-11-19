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
    /// 有一件事它刻意不做：决定这一格用哪张画。哪张图是服务端算的（特写、线稿、像素表情、
    /// 常态的候选链），客户端只负责把 face 字段给的那一条下载下来。分镜逻辑在两边各写一份
    /// 之后，每改一次镜头就要改两处，而那正是这个客户端最容易烂掉的方式。
    ///
    /// 轮询就够。拍与拍之间默认 1.5 秒，比它更快的推送不会让人看到更多东西。
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
            public string Face;
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

                if (card.Face != m.face)
                {
                    card.Face = m.face;
                    StartCoroutine(LoadPortrait(card, m.face));
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
            var root = new GameObject("卡", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
            root.transform.SetParent(_row, false);

            var bg = root.GetComponent<Image>();
            bg.color = new Color(0.11f, 0.12f, 0.15f, 1f);
            bg.raycastTarget = false;

            var layout = root.GetComponent<VerticalLayoutGroup>();
            layout.spacing = 6f;
            layout.padding = new RectOffset(8, 8, 8, 8);
            layout.childAlignment = TextAnchor.UpperCenter;
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            var portraitGo = new GameObject("立绘", typeof(RectTransform), typeof(Image));
            portraitGo.transform.SetParent(root.transform, false);
            var portrait = portraitGo.GetComponent<Image>();
            portrait.preserveAspect = true;
            portrait.raycastTarget = false;
            var portraitLe = portraitGo.AddComponent<LayoutElement>();
            portraitLe.flexibleHeight = 1f;

            var nameGo = new GameObject("名字", typeof(RectTransform));
            nameGo.transform.SetParent(root.transform, false);
            var nameText = nameGo.AddComponent<Text>();
            nameText.font = _font;
            nameText.fontSize = 20;
            nameText.color = Color.white;
            nameText.alignment = TextAnchor.MiddleCenter;
            nameText.raycastTarget = false;
            var nameLe = nameGo.AddComponent<LayoutElement>();
            nameLe.minHeight = 26f;
            nameLe.preferredHeight = 26f;

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
            var lineLe = lineGo.AddComponent<LayoutElement>();
            lineLe.minHeight = 44f;
            lineLe.preferredHeight = 44f;

            return new Card { Root = root, Portrait = portrait, Name = nameText, Line = lineText };
        }

        IEnumerator LoadPortrait(Card card, string path)
        {
            if (_sprites.TryGetValue(path, out var cached))
            {
                card.Portrait.sprite = cached;
                yield break;
            }

            Texture2D tex = null;
            yield return ClarryApi.GetPortrait(path, t => tex = t);
            if (tex == null) yield break;

            var sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0f), 100f);
            _sprites[path] = sprite;
            card.Portrait.sprite = sprite;
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
