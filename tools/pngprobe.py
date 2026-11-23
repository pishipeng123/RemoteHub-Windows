import sys, zlib, struct

def load_png(path):
    data = open(path, 'rb').read()
    assert data[:8] == b'\x89PNG\r\n\x1a\n'
    pos = 8
    w = h = bitd = ctype = None
    idat = b''
    pal = None
    while pos < len(data):
        ln = struct.unpack('>I', data[pos:pos+4])[0]
        typ = data[pos+4:pos+8]
        chunk = data[pos+8:pos+8+ln]
        pos += 12 + ln
        if typ == b'IHDR':
            w, h, bitd, ctype, comp, filt, inter = struct.unpack('>IIBBBBB', chunk)
            assert inter == 0, 'interlaced unsupported'
        elif typ == b'PLTE':
            pal = chunk
        elif typ == b'IDAT':
            idat += chunk
        elif typ == b'IEND':
            break
    raw = zlib.decompress(idat)
    ch = {0:1, 2:3, 3:1, 4:2, 6:4}[ctype]
    bpp = max(1, ch * bitd // 8)
    stride = (w * ch * bitd + 7) // 8
    out = bytearray(h * stride)
    prev = bytearray(stride)
    p = 0
    for y in range(h):
        f = raw[p]; p += 1
        line = bytearray(raw[p:p+stride]); p += stride
        if f == 1:
            for i in range(bpp, stride):
                line[i] = (line[i] + line[i-bpp]) & 0xFF
        elif f == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif f == 3:
            for i in range(stride):
                a = line[i-bpp] if i >= bpp else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 0xFF
        elif f == 4:
            for i in range(stride):
                a = line[i-bpp] if i >= bpp else 0
                b = prev[i]
                c = prev[i-bpp] if i >= bpp else 0
                pa, pb, pc = abs(b-c), abs(a-c), abs(a+b-2*c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pr) & 0xFF
        out[y*stride:(y+1)*stride] = line
        prev = line
    return w, h, ctype, bitd, ch, stride, bytes(out), pal

def pixel(w, ctype, bitd, ch, stride, buf, pal, x, y):
    if bitd == 8:
        off = y*stride + x*ch
        vals = buf[off:off+ch]
        if ctype == 3:
            i = vals[0]*3
            return (pal[i], pal[i+1], pal[i+2])
        if ctype in (0, 4):
            return (vals[0], vals[0], vals[0])
        return (vals[0], vals[1], vals[2])
    elif bitd == 4:
        off = y*stride + (x >> 1)
        b = buf[off]
        v = (b >> 4) if (x & 1) == 0 else (b & 0xF)
        if ctype == 3:
            i = v*3
            return (pal[i], pal[i+1], pal[i+2])
        g = v * 17
        return (g, g, g)
    raise SystemExit('unsupported bit depth %d' % bitd)

if __name__ == '__main__':
    path = sys.argv[1]
    xs = [int(v) for v in sys.argv[2].split(',')] if len(sys.argv) > 2 else [250]
    y0, y1 = (int(sys.argv[3]), int(sys.argv[4])) if len(sys.argv) > 4 else (0, 0)
    w, h, ctype, bitd, ch, stride, buf, pal = load_png(path)
    print('image %dx%d type=%d depth=%d ch=%d' % (w, h, ctype, bitd, ch))
    if y1 <= 0:
        y1 = h - 1
    for y in range(y0, y1+1):
        parts = []
        for x in xs:
            if 0 <= x < w:
                r, g, b = pixel(w, ctype, bitd, ch, stride, buf, pal, x, y)
                parts.append('x%d=%02X%02X%02X' % (x, r, g, b))
        print('y=%3d  %s' % (y, '  '.join(parts)))
