using UnityEngine;
using UnityEngine.EventSystems;

namespace TerrainLab
{
    internal sealed class TerrainParameterTooltip : MonoBehaviour,
        IPointerEnterHandler,
        IPointerExitHandler,
        IPointerClickHandler,
        ISelectHandler,
        IDeselectHandler
    {
        private string _nameKey;
        private string _descriptionKey;
        private bool _pointerInside;
        private bool _selected;

        public void Configure(string nameKey, string descriptionKey)
        {
            _nameKey = nameKey;
            _descriptionKey = descriptionKey;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _pointerInside = true;
            Show();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _pointerInside = false;
            HideWhenInactive();
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            Show();
        }

        public void OnSelect(BaseEventData eventData)
        {
            _selected = true;
            Show();
        }

        public void OnDeselect(BaseEventData eventData)
        {
            _selected = false;
            HideWhenInactive();
        }

        private void OnDisable()
        {
            _pointerInside = false;
            _selected = false;
            Tooltip.hideTooltip();
        }

        private void Show()
        {
            if (!Config.tooltips_active ||
                string.IsNullOrWhiteSpace(_nameKey))
            {
                return;
            }

            Tooltip.show(
                gameObject,
                "tip",
                new TooltipData
                {
                    tip_name = _nameKey,
                    tip_description = _descriptionKey
                });
        }

        private void HideWhenInactive()
        {
            if (!_pointerInside && !_selected)
            {
                Tooltip.hideTooltip();
            }
        }
    }
}
