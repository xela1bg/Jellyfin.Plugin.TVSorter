import hashlib
import json
import os
import sys
import zipfile
from pathlib import Path


def update_package(package_path: Path) -> None:
    temporary_path = package_path.with_suffix(package_path.suffix + ".tmp")
    found_manifest = False
    found_image = False

    try:
        with zipfile.ZipFile(package_path, "r") as source, zipfile.ZipFile(temporary_path, "w") as target:
            for entry in source.infolist():
                content = source.read(entry.filename)
                if entry.filename == "meta.json":
                    metadata = json.loads(content)
                    metadata["imagePath"] = "image.png"
                    content = json.dumps(metadata, indent=4, sort_keys=True).encode("utf-8")
                    found_manifest = True
                elif entry.filename == "image.png":
                    found_image = True

                target.writestr(entry, content)

        if not found_manifest or not found_image:
            raise ValueError("JPRM package must contain meta.json and image.png.")

        os.replace(temporary_path, package_path)
    finally:
        if temporary_path.exists():
            temporary_path.unlink()

    checksum = hashlib.md5(package_path.read_bytes()).hexdigest()
    checksum_path = package_path.with_suffix(package_path.suffix + ".md5sum")
    checksum_path.write_text(f"{checksum} *{package_path.name}\n", encoding="ascii")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: add_local_image_path.py <plugin-zip>")

    update_package(Path(sys.argv[1]))