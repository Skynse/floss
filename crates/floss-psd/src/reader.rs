use std::io::{self, Read, Seek, SeekFrom};

use rayon::prelude::*;

pub struct PsdDocument {
    pub width: i32,
    pub height: i32,
    pub layers: Vec<PsdNode>,
}

#[derive(Clone)]
pub enum PsdNode { Layer(PsdLayer), Group(PsdGroup) }

#[derive(Clone)]
pub struct PsdLayer {
    pub name: String,
    pub visible: bool,
    pub opacity: u8,
    pub clipping: bool,
    pub blend_mode: String,
    pub left: i32,
    pub top: i32,
    pub right: i32,
    pub bottom: i32,
    pub bgra: Vec<u8>,
}

#[derive(Clone)]
pub struct PsdGroup {
    pub name: String,
    pub visible: bool,
    pub opacity: u8,
    pub clipping: bool,
    pub blend_mode: String,
    pub is_open: bool,
    pub children: Vec<PsdNode>,
}

pub fn read_psd<R: Read + Seek>(stream: &mut R) -> io::Result<PsdDocument> {
    let mut r = BEReader::new(stream);

    let mut sig = [0u8; 4];
    r.read_exact(&mut sig)?;
    if sig != *b"8BPS" {
        return Err(io::Error::new(io::ErrorKind::InvalidData, "Not a PSD file"));
    }
    let ver = r.u16()?;
    if ver != 1 {
        return Err(io::Error::new(io::ErrorKind::Unsupported, "PSB not supported"));
    }
    r.skip(6)?;
    let _ = r.u16()?;
    let height = r.u32()? as i32;
    let width = r.u32()? as i32;
    let bit_depth = r.u16()?;
    let _color_mode = r.u16()?;
    let n = r.u32()? as i64; r.skip(n)?;
    let n = r.u32()? as i64; r.skip(n)?;

    let mut b4 = [0u8; 4];

    let layer_mask_len = r.u32()? as u64;
    if layer_mask_len == 0 {
        return Ok(PsdDocument { width, height, layers: vec![] });
    }
    let layer_mask_end = r.stream_position()? + layer_mask_len;

    let layer_info_len = r.u32()? as u64;
    if layer_info_len == 0 {
        r.seek(SeekFrom::Start(layer_mask_end))?;
        return Ok(PsdDocument { width, height, layers: vec![] });
    }
    let layer_count = (r.i16()?.unsigned_abs()) as usize;

    // ── Pass 1: read metadata ──────────────────────────────────────────
    let mut records: Vec<LayerRecord> = Vec::with_capacity(layer_count);

    for _ in 0..layer_count {
        let mut rec = LayerRecord::default();
        rec.top = r.i32()?;
        rec.left = r.i32()?;
        rec.bottom = r.i32()?;
        rec.right = r.i32()?;

        let cn = r.u16()? as usize;
        for _ in 0..cn {
            rec.ch_ids.push(r.i16()?);
            rec.ch_lens.push(r.u32()? as u64);
        }

        r.read_exact(&mut b4)?;
        r.read_exact(&mut b4)?;
        rec.blend = String::from_utf8_lossy(&b4).into();
        rec.opacity = r.byte()?;
        rec.clip = r.byte()? != 0;
        rec.visible = (r.byte()? & 2) == 0;
        r.skip(1)?;

        let ext_len = r.u32()? as u64;
        let ext_end = r.stream_position()? + ext_len;
        let n = r.u32()? as i64; r.skip(n)?;
        let n = r.u32()? as i64; r.skip(n)?;

        let nlen = r.byte()? as usize;
        let mut nb = vec![0u8; nlen];
        r.read_exact(&mut nb)?;
        rec.name = String::from_utf8_lossy(&nb).into();
        r.skip(((4 - ((nlen + 1) & 3)) & 3) as i64)?;

        while r.stream_position()? + 11 < ext_end {
            r.read_exact(&mut b4)?;
            if b4[0] != b'8' || (b4[1] != b'B' && b4[1] != b'8') { break; }
            r.read_exact(&mut b4)?;
            let al = r.u32()? as u64;
            let ae = r.stream_position()? + al;
            if &b4 == b"lsct" || &b4 == b"lsdk" {
                rec.section = r.u32()? as i32;
                if al >= 12 {
                    r.skip(4)?;
                    r.read_exact(&mut b4)?;
                    rec.div_blend = String::from_utf8_lossy(&b4).into();
                }
            }
            r.seek(SeekFrom::Start(ae))?;
        }
        r.seek(SeekFrom::Start(ext_end))?;
        records.push(rec);
    }

    // ── Pass 2: read raw channel data ──────────────────────────────────
    for i in 0..layer_count {
        let lw = records[i].right - records[i].left;
        let lh = records[i].bottom - records[i].top;
        let nc = records[i].ch_ids.len();
        for c in 0..nc {
            let id = records[i].ch_ids[c];
            let len = records[i].ch_lens[c] as usize;
            if lw > 0 && lh > 0 && len > 0 {
                let mut raw = vec![0u8; len];
                r.read_exact(&mut raw)?;
                records[i].ch_raw.push(ChanBuf { id, data: raw });
            } else if len > 0 {
                r.skip(len as i64)?;
            }
        }
    }
    r.seek(SeekFrom::Start(layer_mask_end))?;

    // ── Pass 3: parallel decode + BGRA ─────────────────────────────────
    records.par_iter_mut().for_each(|rec| {
        let lw = rec.right - rec.left;
        let lh = rec.bottom - rec.top;
        if lw <= 0 || lh <= 0 || rec.section != 0 { return; }
        let w = lw as usize;
        let h = lh as usize;
        if w == 0 || h == 0 { return; }
        let px = w * h;
        let mut p: [Option<Vec<u8>>; 4] = [None, None, None, None];
        for cb in &rec.ch_raw {
            if cb.data.len() < 2 { continue; }
            let comp = ((cb.data[0] as u16) << 8) | cb.data[1] as u16;
            if let Some(plane) = decode_plane(&cb.data, 2, comp, w as i32, h as i32, bit_depth as i32) {
                match cb.id { 0 => p[0]=Some(plane), 1=>p[1]=Some(plane), 2=>p[2]=Some(plane), -1=>p[3]=Some(plane), _=>{} }
            }
        }
        rec.ch_raw.clear();
        if p.iter().any(|o| o.is_some()) {
            let mut bgra = vec![0u8; px * 4];
            assemble_bgra(&p, px, &mut bgra);
            rec.bgra = Some(bgra);
        }
    });

    Ok(build_tree(&records, width, height))
}

// ═══════════════════════════════════════════════════════════════════════════════
// LayerRecord
// ═══════════════════════════════════════════════════════════════════════════════

struct LayerRecord {
    top: i32, left: i32, bottom: i32, right: i32,
    ch_ids: Vec<i16>, ch_lens: Vec<u64>, ch_raw: Vec<ChanBuf>,
    blend: String, div_blend: String,
    opacity: u8, clip: bool, visible: bool,
    name: String, section: i32,
    bgra: Option<Vec<u8>>,
}

struct ChanBuf { id: i16, data: Vec<u8> }

impl Default for LayerRecord {
    fn default() -> Self { Self {
        top: 0, left: 0, bottom: 0, right: 0,
        ch_ids: vec![], ch_lens: vec![], ch_raw: vec![],
        blend: "norm".into(), div_blend: String::new(),
        opacity: 255, clip: false, visible: true,
        name: String::new(), section: 0, bgra: None,
    }}
}

// ═══════════════════════════════════════════════════════════════════════════════
// Decode
// ═══════════════════════════════════════════════════════════════════════════════

fn decode_plane(raw: &[u8], off: usize, comp: u16, w: i32, h: i32, bd: i32) -> Option<Vec<u8>> {
    let span = &raw[off..];
    match comp { 0=>decode_raw(span,w,h,bd), 1=>decode_rle(span,w,h,bd), 2=>decode_zip(span,w,h,bd,false), 3=>decode_zip(span,w,h,bd,true), _=>None }
}

fn decode_raw(span: &[u8], w: i32, h: i32, bd: i32) -> Option<Vec<u8>> {
    let px = (w*h) as usize;
    let mut o = vec![0u8; px];
    if bd == 8 { let n=px.min(span.len()); o[..n].copy_from_slice(&span[..n]); }
    else if bd == 16 { for i in 0..px.min(span.len()/2) { o[i]=span[i*2]; } }
    Some(o)
}

fn decode_rle(span: &[u8], w: i32, h: i32, bd: i32) -> Option<Vec<u8>> {
    let h = h as usize; let w = w as usize;
    let ppr = if bd == 8 { w } else { w * (bd as usize/8) };
    let px = w*h;
    let mut plane = vec![0u8; px];
    let mut row = vec![0u8; ppr];
    let mut off = h*2;
    for y in 0..h {
        if off+2 > span.len() { break; }
        let rl = ((span[y*2] as u16) << 8 | span[y*2+1] as u16) as usize;
        if rl == 0 || off+rl > span.len() { break; }
        row.fill(0);
        unpack_bits(&span[off..off+rl], &mut row, ppr);
        off += rl;
        if bd == 8 { let n=w.min(px-y*w); plane[y*w..y*w+n].copy_from_slice(&row[..n]); }
        else { for x in 0..w { if x*2<row.len() && y*w+x<px { plane[y*w+x]=row[x*2]; } } }
    }
    Some(plane)
}

fn unpack_bits(src: &[u8], dst: &mut [u8], max: usize) {
    let mut s=0; let mut d=0;
    while s<src.len() && d<max {
        let n=src[s] as i8; s+=1;
        if n>=0 { let c=n as usize+1; let e=s.saturating_add(c).min(src.len()); while s<e && d<max { dst[d]=src[s]; d+=1; s+=1; } }
        else if n!=-128 { if s<src.len() { let v=src[s]; s+=1; for _ in 0..(1-n as i32) { if d>=max { break; } dst[d]=v; d+=1; } } }
    }
}

fn decode_zip(span: &[u8], w: i32, h: i32, bd: i32, delta: bool) -> Option<Vec<u8>> {
    let px = (w*h) as usize;
    let bps = (bd/8) as usize;
    let want = px*bps;
    let mut data = zlib_inflate(span, want)?;
    if delta { apply_delta(&mut data, w as usize, bd); }
    if bd == 8 { Some(data) }
    else { let mut o=vec![0u8;px]; for i in 0..px { o[i]=data[i*bps]; } Some(o) }
}

fn zlib_inflate(data: &[u8], want: usize) -> Option<Vec<u8>> {
    use flate2::read::ZlibDecoder;
    let mut d = ZlibDecoder::new(data);
    let mut buf = Vec::with_capacity(want);
    d.read_to_end(&mut buf).ok()?;
    if buf.len() < want { buf.resize(want, 0); } else { buf.truncate(want); }
    Some(buf)
}

fn apply_delta(data: &mut [u8], _w: usize, bd: i32) {
    if bd==8 { for x in 1..data.len() { data[x]=data[x].wrapping_add(data[x-1]); } }
    else if bd==16 { for x in (2..data.len()).step_by(2) { data[x]=data[x].wrapping_add(data[x-2]); } }
}

// ═══════════════════════════════════════════════════════════════════════════════
// BGRA
// ═══════════════════════════════════════════════════════════════════════════════

fn assemble_bgra(p: &[Option<Vec<u8>>; 4], px: usize, out: &mut [u8]) {
    let r = p[0].as_ref().map(|v|v.as_slice());
    let g = p[1].as_ref().map(|v|v.as_slice());
    let b = p[2].as_ref().map(|v|v.as_slice());
    let a = p[3].as_ref().map(|v|v.as_slice());
    for i in 0..px {
        out[i*4]=b.and_then(|s|s.get(i)).copied().unwrap_or(0);
        out[i*4+1]=g.and_then(|s|s.get(i)).copied().unwrap_or(0);
        out[i*4+2]=r.and_then(|s|s.get(i)).copied().unwrap_or(0);
        out[i*4+3]=a.and_then(|s|s.get(i)).copied().unwrap_or(255);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
// Tree
// ═══════════════════════════════════════════════════════════════════════════════

fn build_tree(records: &[LayerRecord], ww: i32, hh: i32) -> PsdDocument {
    let mut cur = vec![];
    let mut stack: Vec<(PsdGroup, Vec<PsdNode>)> = vec![];
    for rec in records.iter() {
        match rec.section {
            3 => {
                let g = PsdGroup { name:String::new(),visible:true,opacity:255,clipping:false,blend_mode:"pass".into(),is_open:true,children:vec![] };
                let par = std::mem::take(&mut cur);
                stack.push((g, par));
            }
            1|2 => {
                let children = std::mem::take(&mut cur);
                if let Some((mut g, mut par)) = stack.pop() {
                    g.children = children;
                    g.name.clone_from(&rec.name);
                    g.visible=rec.visible; g.opacity=rec.opacity; g.clipping=rec.clip;
                    g.blend_mode = if rec.div_blend.is_empty() { rec.blend.clone() } else { rec.div_blend.clone() };
                    g.is_open = rec.section == 1;
                    par.push(PsdNode::Group(g));
                    cur = par;
                }
            }
            _ => {
                let bgra = rec.bgra.clone().unwrap_or_else(|| {
                    let rw=((rec.right-rec.left) as usize).max(1);
                    let rh=((rec.bottom-rec.top) as usize).max(1);
                    vec![0u8; rw*rh*4]
                });
                cur.push(PsdNode::Layer(PsdLayer {
                    name:rec.name.clone(),visible:rec.visible,opacity:rec.opacity,
                    clipping:rec.clip,blend_mode:rec.blend.clone(),
                    left:rec.left,top:rec.top,right:rec.right,bottom:rec.bottom,bgra,
                }));
            }
        }
    }
    while let Some((g, mut par)) = stack.pop() { par.push(PsdNode::Group(g)); if stack.is_empty() { cur=par; } }
    PsdDocument { width: ww, height: hh, layers: cur }
}

// ═══════════════════════════════════════════════════════════════════════════════
// BEReader
// ═══════════════════════════════════════════════════════════════════════════════

struct BEReader<R: Read + Seek> { inner: R }

impl<R: Read + Seek> BEReader<R> {
    fn new(inner: R) -> Self { Self { inner } }
    fn read_exact(&mut self, buf: &mut [u8]) -> io::Result<()> { self.inner.read_exact(buf) }
    fn byte(&mut self) -> io::Result<u8> { let mut b=[0u8]; self.inner.read_exact(&mut b)?; Ok(b[0]) }
    fn i16(&mut self) -> io::Result<i16> { let mut b=[0u8;2]; self.inner.read_exact(&mut b)?; Ok(i16::from_be_bytes(b)) }
    fn u16(&mut self) -> io::Result<u16> { let mut b=[0u8;2]; self.inner.read_exact(&mut b)?; Ok(u16::from_be_bytes(b)) }
    fn i32(&mut self) -> io::Result<i32> { let mut b=[0u8;4]; self.inner.read_exact(&mut b)?; Ok(i32::from_be_bytes(b)) }
    fn u32(&mut self) -> io::Result<u32> { let mut b=[0u8;4]; self.inner.read_exact(&mut b)?; Ok(u32::from_be_bytes(b)) }
    fn skip(&mut self, n: i64) -> io::Result<()> { if n>0 { self.inner.seek(SeekFrom::Current(n))?; } Ok(()) }
    fn stream_position(&mut self) -> io::Result<u64> { self.inner.stream_position() }
}

impl<R: Read + Seek> Seek for BEReader<R> {
    fn seek(&mut self, pos: SeekFrom) -> io::Result<u64> { self.inner.seek(pos) }
}
