"""PNG 한 장으로 Windows .ico (16·32·48·256px, PNG 압축 항목) 를 만든다.

macOS 기본 도구(sips)로 크기를 맞추고, ICO 헤더만 직접 쓴다. 외부 라이브러리 불필요.
Usage: python3 scripts/make_ico.py <in.png> <out.ico>
"""
import os
import struct
import subprocess
import sys
import tempfile

SIZES = [16, 32, 48, 256]


def main(source: str, target: str) -> None:
    with tempfile.TemporaryDirectory() as work:
        square = os.path.join(work, "square.png")
        # 정사각 투명 캔버스에 가운데 맞춤 (비율 유지)
        subprocess.run(["sips", "-s", "format", "png", "--padToHeightWidth", "440", "440",
                        source, "--out", square], check=True, capture_output=True)
        images = []
        for size in SIZES:
            path = os.path.join(work, f"{size}.png")
            subprocess.run(["sips", "-z", str(size), str(size), square, "--out", path],
                           check=True, capture_output=True)
            with open(path, "rb") as f:
                images.append((size, f.read()))

    header = struct.pack("<HHH", 0, 1, len(images))
    offset = len(header) + 16 * len(images)
    entries, payload = b"", b""
    for size, data in images:
        dimension = 0 if size >= 256 else size  # 256 은 0 으로 표기
        entries += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(data), offset)
        payload += data
        offset += len(data)

    with open(target, "wb") as f:
        f.write(header + entries + payload)
    print(f"✓ {target} ({', '.join(str(s) for s in SIZES)}px)")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
