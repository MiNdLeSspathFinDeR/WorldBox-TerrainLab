import argparse
import json
from time import sleep
from typing import Any, Dict, Iterable, Iterator, Optional, Tuple

from pathlib import Path
from PIL import Image as Img

from . import convert, fit_map_size_to_budget
from .calibration import (
    classification_profile_path,
    load_classification_profile,
)
from .consts import SAFE_TILES_TUPLE, TILES_TUPLE
from .saves import (
    find_worldbox_stats_template,
    next_worldbox_save_slot,
    resolve_worldbox_saves_dir,
    write_game_save_atomically,
    write_map_folder,
)
from .utils import json_loads

# fmt: off
IMAGE_FORMATS = {".apng", ".blp", ".bmp", ".bufr", ".bw", ".dds", ".dib", ".emf", ".eps", ".gif", ".grib", ".h5", ".hdf", ".icb", ".icns", ".ico", ".im", ".j2c", ".j2k", ".jfif", ".jp2", ".jpc", ".jpe", ".jpeg", ".jpf", ".jpg", ".jpx", ".msp", ".pbm", ".pcx", ".pgm", ".png", ".pnm", ".ppm", ".ps", ".rgb", ".rgba", ".sgi", ".tga", ".tif", ".tiff", ".vda", ".vst", ".webp", ".wmf", ".xbm"}
# fmt: on
GENERATED_PREVIEWS = {"preview.png", "preview_small.png"}
parser = argparse.ArgumentParser(
    prog="ImageToMap",
    description="Convert images to WorldBox maps",
    epilog="",
)
parser.add_argument(
    "images",
    help="Image files to convert to maps. If this option is supplied with folders, the program will search for images inside them",
    nargs="*",
    type=Path,
)
parser.add_argument(
    "--no-config",
    help="Ignore the config file",
    action="store_true",
    default=False,
)
parser.add_argument(
    "--generate-config",
    help="Generate a config file in the current directory",
    action="store_true",
    default=False,
)
parser.add_argument(
    "-D",
    "--dither",
    help="Enable dithering for smoother colour transitions. Not recommended for maps designed to actually be played with",
    action="store_true",
    default=False,
)
parser.add_argument(
    "--algorithm",
    help="Conversion algorithm: adaptive playable terrain or direct palette matching",
    choices=("terrain", "palette"),
    default="terrain",
)
parser.add_argument(
    "--palette",
    help="Tile palette to use when no config file overrides tiles",
    choices=("safe", "full"),
    default="safe",
)
parser.add_argument(
    "--terrain-clusters",
    help="Number of adaptive terrain color clusters",
    type=int,
    default=14,
)
parser.add_argument(
    "--terrain-smooth",
    help="Noise cleanup passes for the adaptive terrain algorithm",
    type=int,
    default=1,
)
parser.add_argument(
    "--terrain-min-region",
    help="Remove isolated land regions smaller than this many tiles",
    type=int,
    default=32,
)
parser.add_argument(
    "-W",
    "--width",
    help="Target width of the map(s). Default to auto",
    type=int,
    default=0,
)
parser.add_argument(
    "-H",
    "--height",
    help="Target height of the map(s). Default to auto",
    type=int,
    default=0,
)
parser.add_argument(
    "-R",
    "--recursive",
    help="Enable recursive searching inside folders",
    action="store_true",
    default=False,
)
parser.add_argument(
    "-O",
    "--output",
    help="Where to save the converted maps and previews. Default to the current directory",
    type=Path,
    default="./",
)
parser.add_argument(
    "--map-name",
    help="World name stored in generated map metadata. Defaults to the image file name",
    type=str,
    default=None,
)
parser.add_argument(
    "--save-to-game",
    help="Save converted maps directly into the WorldBox saves folder as new save slots",
    action="store_true",
    default=False,
)
parser.add_argument(
    "--game-saves-dir",
    help="Override the WorldBox saves folder. Implies --save-to-game",
    type=Path,
    default=None,
)
parser.add_argument(
    "--watch",
    help="Keep running and convert images when they are added or changed",
    action="store_true",
    default=False,
)
parser.add_argument(
    "--watch-interval",
    help="Seconds between workspace scans in --watch mode",
    type=float,
    default=2.0,
)
parser.add_argument(
    "--fit-budget",
    help="Fit automatic dimensions to the largest aspect-preserving TerrainLab map",
    action="store_true",
    default=False,
)
parser.add_argument(
    "--classification-profile",
    help=(
        "TerrainLab manual classification JSON. If omitted, "
        "<image>.terrainlab-classification.json is discovered automatically"
    ),
    type=Path,
    default=None,
)
parser.add_argument(
    "--no-classification-profile",
    help=argparse.SUPPRESS,
    action="store_true",
    default=False,
)
parser.add_argument(
    "--strict",
    help=argparse.SUPPRESS,
    action="store_true",
    default=False,
)


def process_args() -> Dict[str, Any]:
    args = {}
    parsed_args = parser.parse_args()
    if parsed_args.game_saves_dir is not None:
        parsed_args.save_to_game = True
    if parsed_args.generate_config:
        with open("itm_config.json", "w+") as config_file:
            default_config = {
                "output": "./",
                "algorithm": "terrain",
                "palette": "safe",
                "terrain_clusters": 14,
                "terrain_smooth": 1,
                "terrain_min_region": 32,
                "tiles": {tile: tile in SAFE_TILES_TUPLE for tile in TILES_TUPLE},
            }
            json.dump(default_config, config_file, indent=4)

    for arg in (
        "images",
        "algorithm",
        "palette",
        "terrain_clusters",
        "terrain_smooth",
        "terrain_min_region",
        "dither",
        "width",
        "height",
        "recursive",
        "output",
        "map_name",
        "save_to_game",
        "game_saves_dir",
        "watch",
        "watch_interval",
        "fit_budget",
        "classification_profile",
        "no_classification_profile",
        "strict",
    ):
        args[arg] = getattr(parsed_args, arg)

    config_path = Path("itm_config.json")
    if not parsed_args.no_config and config_path.exists():
        config = json_loads(config_path.read_bytes())

        args["output"] = Path(config.get("output", "./"))
        args["algorithm"] = config.get("algorithm", args["algorithm"])
        args["palette"] = config.get("palette", args["palette"])
        args["terrain_clusters"] = config.get(
            "terrain_clusters", args["terrain_clusters"]
        )
        args["terrain_smooth"] = config.get(
            "terrain_smooth", args["terrain_smooth"]
        )
        args["terrain_min_region"] = config.get(
            "terrain_min_region", args["terrain_min_region"]
        )
        configured_tiles = config.get("tiles")
        if isinstance(configured_tiles, dict):
            selected_tiles = [
                tile
                for tile, enabled in configured_tiles.items()
                if enabled is True and tile in TILES_TUPLE
            ]
        else:
            selected_tiles = list(TILES_TUPLE)

        if args["palette"] == "safe":
            safe_tiles = set(SAFE_TILES_TUPLE)
            selected_tiles = [tile for tile in selected_tiles if tile in safe_tiles]
        args["tiles"] = selected_tiles

    else:
        args["tiles"] = TILES_TUPLE if args["palette"] == "full" else SAFE_TILES_TUPLE

    if not args["tiles"]:
        parser.error("no WorldBox tiles are enabled by the selected palette/config")
    if args["algorithm"] not in {"terrain", "palette"}:
        parser.error("algorithm must be either 'terrain' or 'palette'")
    if args["palette"] not in {"safe", "full"}:
        parser.error("palette must be either 'safe' or 'full'")
    if not 4 <= args["terrain_clusters"] <= 64:
        parser.error("--terrain-clusters must be between 4 and 64")
    if not 0 <= args["terrain_smooth"] <= 8:
        parser.error("--terrain-smooth must be between 0 and 8")
    if not 0 <= args["terrain_min_region"] <= 4096:
        parser.error("--terrain-min-region must be between 0 and 4096")
    if args["watch_interval"] <= 0:
        parser.error("--watch-interval must be greater than 0")

    return args


def is_image_file(path: Path) -> bool:
    if not path.is_file() or path.suffix.lower() not in IMAGE_FORMATS:
        return False

    return not (path.name in GENERATED_PREVIEWS and (path.parent / "map.wbox").exists())


def iter_image_paths(
    paths: Iterable[Path],
    recursive: bool,
    report_missing: bool = True,
) -> Iterator[Path]:
    for path in paths:
        if not path.exists():
            if report_missing:
                print(f"Error: '{path}' does not exist")
            continue

        if path.is_dir():
            pattern = "**/*" if recursive else "*"
            for glob_path in sorted(path.glob(pattern)):
                if is_image_file(glob_path):
                    yield glob_path

        elif is_image_file(path):
            yield path


def image_fingerprint(path: Path) -> Optional[Tuple[int, int]]:
    try:
        stat = path.stat()
    except OSError:
        return None

    return stat.st_mtime_ns, stat.st_size


def get_map_name(image_path: Path, args: Dict[str, Any]) -> str:
    return args["map_name"] or image_path.stem


def get_output_path(image_path: Path, args: Dict[str, Any]) -> Path:
    if args["save_to_game"]:
        return next_worldbox_save_slot(args["game_saves_dir"])

    return args["output"] / image_path.stem


def process_image(image_path: Path, args: Dict[str, Any], tiles: Iterable[str]) -> None:
    map_name = get_map_name(image_path=image_path, args=args)
    output_path = get_output_path(image_path=image_path, args=args)

    with Img.open(image_path) as image:
        profile_path = (
            None
            if args["no_classification_profile"]
            else args["classification_profile"]
        )
        if profile_path is None and not args["no_classification_profile"]:
            discovered_profile = classification_profile_path(image_path)
            profile_path = discovered_profile if discovered_profile.is_file() else None
        classification_profile = (
            load_classification_profile(profile_path, image.size)
            if profile_path is not None
            else None
        )
        width = args["width"]
        height = args["height"]
        if args["fit_budget"] and width == 0 and height == 0:
            width, height = fit_map_size_to_budget(*image.size)
        converted_map = convert(
            image=image,
            dither=args["dither"],
            width=width,
            height=height,
            tiles=tiles,
            name=map_name,
            algorithm=args["algorithm"],
            terrain_clusters=args["terrain_clusters"],
            terrain_smooth=args["terrain_smooth"],
            terrain_min_region=args["terrain_min_region"],
            classification_profile=classification_profile,
        )

    if args["save_to_game"]:
        write_game_save_atomically(
            output_path=output_path,
            converted_map=converted_map,
            name=map_name,
            stats_template=args.get("stats_template"),
        )
    else:
        write_map_folder(
            output_path=output_path,
            converted_map=converted_map,
            name=map_name,
        )

    manual_status = (
        f", {len(classification_profile.samples)} manual samples + "
        f"{len(classification_profile.regions)} regions + Int16 DEM"
        if classification_profile is not None
        else ""
    )
    print(
        f"Converted {image_path} -> {output_path} "
        f"({converted_map.width}x{converted_map.height} blocks{manual_status})"
    )


def watch_images(args: Dict[str, Any], tiles: Iterable[str]) -> None:
    if not args["images"]:
        args["images"] = [Path(".")]

    seen: Dict[Path, Tuple[int, int]] = {}
    pending: Dict[Path, Tuple[int, int]] = {}

    print("Watching", ", ".join(str(path) for path in args["images"]))
    while True:
        for image_path in iter_image_paths(
            args["images"],
            recursive=args["recursive"],
            report_missing=False,
        ):
            fingerprint = image_fingerprint(image_path)
            if fingerprint is None or seen.get(image_path) == fingerprint:
                continue

            if pending.get(image_path) != fingerprint:
                pending[image_path] = fingerprint
                continue

            try:
                process_image(image_path, args, tiles)
            except Exception as exc:
                print(f"Error converting '{image_path}': {exc}")
            else:
                seen[image_path] = fingerprint
                pending.pop(image_path, None)

        sleep(args["watch_interval"])


def main() -> None:
    args = process_args()
    tiles = args["tiles"]

    if args["save_to_game"]:
        args["game_saves_dir"] = resolve_worldbox_saves_dir(args["game_saves_dir"])
        args["game_saves_dir"].mkdir(exist_ok=True, parents=True)
        args["stats_template"] = find_worldbox_stats_template(args["game_saves_dir"])

    if args["watch"]:
        watch_images(args, tiles)
        return

    for image_path in iter_image_paths(args["images"], args["recursive"]):
        try:
            process_image(image_path, args, tiles)
        except Exception as exc:
            print(f"Error converting '{image_path}': {exc}")
            if args["strict"]:
                raise


if __name__ == "__main__":
    try:
        main()

    except KeyboardInterrupt:
        pass
