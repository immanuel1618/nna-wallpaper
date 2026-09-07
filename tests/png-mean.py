"""Mean brightness (0..255) of a PNG, no third-party modules. Usage: python tests/png-mean.py shot.png [threshold]
Exit 0 when mean > threshold (default 3), 1 otherwise."""
import struct
import sys
import zlib


def png_mean(path):
    data = open(path, 'rb').read()
    assert data[:8] == b'\x89PNG\r\n\x1a\n', 'not a png'
    pos, width, height, bit_depth, color_type, idat = 8, 0, 0, 0, 0, b''
    while pos < len(data):
        length, ctype = struct.unpack('>I4s', data[pos:pos + 8])
        chunk = data[pos + 8:pos + 8 + length]
        if ctype == b'IHDR':
            width, height, bit_depth, color_type = struct.unpack('>IIBB', chunk[:10])
        elif ctype == b'IDAT':
            idat += chunk
        elif ctype == b'IEND':
            break
        pos += 12 + length
    assert bit_depth == 8, 'only 8-bit png supported'
    channels = {0: 1, 2: 3, 4: 2, 6: 4}[color_type]
    raw = zlib.decompress(idat)
    stride = width * channels
    prev = bytearray(stride)
    total, count = 0, 0
    off = 0
    for _ in range(height):
        f = raw[off]
        off += 1
        line = bytearray(raw[off:off + stride])
        off += stride
        for i in range(stride):
            a = line[i - channels] if i >= channels else 0
            b = prev[i]
            c = prev[i - channels] if i >= channels else 0
            if f == 1:
                line[i] = (line[i] + a) & 255
            elif f == 2:
                line[i] = (line[i] + b) & 255
            elif f == 3:
                line[i] = (line[i] + ((a + b) >> 1)) & 255
            elif f == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pr = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 255
        # sample every 4th pixel for speed
        for x in range(0, width, 4):
            px = line[x * channels:(x + 1) * channels]
            rgb = px[:3] if channels >= 3 else px[:1] * 3
            total += (rgb[0] + rgb[1] + rgb[2]) / 3
            count += 1
        prev = line
    return total / max(count, 1)


if __name__ == '__main__':
    threshold = float(sys.argv[2]) if len(sys.argv) > 2 else 3.0
    mean = png_mean(sys.argv[1])
    print(f'{sys.argv[1]}: mean brightness {mean:.2f} ({"PASS" if mean > threshold else "FAIL"}, threshold {threshold})')
    sys.exit(0 if mean > threshold else 1)
