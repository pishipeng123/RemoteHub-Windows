import sys, zlib, struct
from pngprobe import load_png, pixel

def save_png(path, w, h, rows):
    raw = b''.join(b'\x00' + r for r in rows)
    def chunk(t, d):
        c = struct.pack('>I', len(d)) + t + d
        return c + struct.pack('>I', zlib.crc32(t + d) & 0xFFFFFFFF)
    out = b'\x89PNG\r\n\x1a\n'
    out += chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0))
    out += chunk(b'IDAT', zlib.compress(raw, 6))
    out += chunk(b'IEND', b'')
    open(path, 'wb').write(out)

def main(src, dst, x, y, w, h, scale):
    W, H, ctype, bitd, ch, stride, buf, pal = load_png(src)
    rows = []
    for yy in range(y, min(y + h, H)):
        row = bytearray()
        for xx in range(x, min(x + w, W)):
            r, g, b = pixel(W, ctype, bitd, ch, stride, buf, pal, xx, yy)
            row += bytes([r, g, b]) * scale
        for _ in range(scale):
            rows.append(bytes(row))
    save_png(dst, (min(x + w, W) - x) * scale, len(rows), rows)
    print('wrote %s (%dx%d)' % (dst, (min(x + w, W) - x) * scale, len(rows)))

if __name__ == '__main__':
    a = sys.argv
    main(a[1], a[2], int(a[3]), int(a[4]), int(a[5]), int(a[6]), int(a[7]) if len(a) > 7 else 3)
