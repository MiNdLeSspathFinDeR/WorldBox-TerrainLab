from dataclasses import dataclass
from typing import Any, Dict, Optional

from PIL.Image import Image


@dataclass(frozen=True, slots=True)  # type: ignore[call-overload]
class Map:
    data: bytes
    width: int
    height: int
    preview: Image
    elevation: Optional[Any] = None
    classification_profile: Optional[Dict[str, Any]] = None
    clustering_profile: Optional[Dict[str, Any]] = None
