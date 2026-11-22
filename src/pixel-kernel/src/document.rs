use crate::{fill_region, hue_pixel};

struct Frame {
    pixels: Vec<u32>,
    revision: u64,
}

pub struct Document {
    width: usize,
    height: usize,
    current: Frame,
    original: Option<Frame>,
    undo: Vec<Frame>,
    redo: Vec<Frame>,
    clock: u64,
    saved: u64,
    visited: Vec<bool>,
    selection: Option<[usize; 4]>,
}

impl Document {
    fn begin(&mut self, hue: bool) {
        if self.original.is_some() {
            return;
        }
        self.original = Some(Frame {
            pixels: self.current.pixels.clone(),
            revision: self.current.revision,
        });
        if hue {
            self.visited.resize(self.current.pixels.len(), false);
            self.visited.fill(false);
        }
    }

    fn commit(&mut self) {
        let Some(original) = self.original.take() else { return };
        if original.pixels == self.current.pixels {
            return;
        }
        self.clock += 1;
        self.current.revision = self.clock;
        self.redo.clear();
        self.undo.push(original);
        let limit = (96_000_000 / (self.current.pixels.len() * 4)).clamp(1, 24);
        if self.undo.len() > limit {
            self.undo.remove(0);
        }
    }

    fn undo(&mut self) {
        if self.original.is_none() && let Some(frame) = self.undo.pop() {
            self.redo.push(std::mem::replace(&mut self.current, frame));
        }
    }

    fn redo(&mut self) {
        if self.original.is_none() && let Some(frame) = self.redo.pop() {
            self.undo.push(std::mem::replace(&mut self.current, frame));
        }
    }

    fn bounds(&self) -> [usize; 4] {
        self.selection.unwrap_or([0, 0, self.width, self.height])
    }

    fn stamp(&mut self, x: i32, y: i32, size: i32, mode: u32, color: u32, angle: i32) {
        let size = size.clamp(1, 64);
        let inset = (size - 1) / 2;
        let origin_x = x - inset;
        let origin_y = y - inset;
        let [left, top, right, bottom] = self.bounds().map(|v| v as i32);
        for row in origin_y.max(top)..(origin_y + size).min(bottom) {
            for col in origin_x.max(left)..(origin_x + size).min(right) {
                let dx = 2 * (col - origin_x) - (size - 1);
                let dy = 2 * (row - origin_y) - (size - 1);
                if dx * dx + dy * dy > size * size {
                    continue;
                }
                let at = row as usize * self.width + col as usize;
                self.current.pixels[at] = match mode {
                    1 => 0,
                    2 if !self.visited[at] => {
                        self.visited[at] = true;
                        hue_pixel(self.current.pixels[at], angle)
                    }
                    2 => continue,
                    _ => color,
                };
            }
        }
    }
}

thread_local! {
    static DOCUMENT: std::cell::RefCell<Option<Document>> = const { std::cell::RefCell::new(None) };
}

fn with_document<T: Default>(operation: impl FnOnce(&mut Document) -> T) -> T {
    DOCUMENT.with_borrow_mut(|value| value.as_mut().map(operation).unwrap_or_default())
}

#[unsafe(no_mangle)]
pub extern "C" fn image_open(width: usize, height: usize) -> *mut u32 {
    let Some(length) = width.checked_mul(height).filter(|n| (1..=16_777_216).contains(n)) else {
        return std::ptr::null_mut();
    };
    let mut pixels = vec![0; length];
    let pointer = pixels.as_mut_ptr();
    DOCUMENT.with_borrow_mut(|current| {
        *current = Some(Document {
            width, height,
            current: Frame { pixels, revision: 0 },
            original: None,
            undo: Vec::new(), redo: Vec::new(),
            clock: 0, saved: 0,
            visited: Vec::new(),
            selection: None,
        });
    });
    pointer
}

#[unsafe(no_mangle)]
pub extern "C" fn image_pixels() -> usize {
    with_document(|doc| doc.current.pixels.as_ptr() as usize)
}

#[unsafe(no_mangle)]
pub extern "C" fn image_state() -> u32 {
    with_document(|doc| u32::from(!doc.undo.is_empty())
        | u32::from(!doc.redo.is_empty()) << 1
        | u32::from(doc.current.revision != doc.saved || doc.original.is_some()) << 2)
}

#[unsafe(no_mangle)]
pub extern "C" fn image_open_edit(hue: bool) {
    with_document(|doc| doc.begin(hue));
}

#[unsafe(no_mangle)]
pub extern "C" fn image_commit() {
    with_document(Document::commit);
}

#[unsafe(no_mangle)]
pub extern "C" fn image_reset_preview() {
    with_document(|doc| {
        if let Some(frame) = &doc.original {
            doc.current.pixels.copy_from_slice(&frame.pixels);
        }
    });
}

#[unsafe(no_mangle)]
pub extern "C" fn image_abort() {
    with_document(|doc| {
        if let Some(frame) = doc.original.take() {
            doc.current = frame;
        }
    });
}

#[unsafe(no_mangle)]
pub extern "C" fn image_undo() {
    with_document(Document::undo);
}

#[unsafe(no_mangle)]
pub extern "C" fn image_redo() {
    with_document(Document::redo);
}

#[unsafe(no_mangle)]
pub extern "C" fn image_pick(x: usize, y: usize) -> u32 {
    with_document(|doc| {
        if x < doc.width && y < doc.height { doc.current.pixels[y * doc.width + x] }
        else { 0 }
    })
}

#[unsafe(no_mangle)]
pub extern "C" fn image_select(left: usize, top: usize, right: usize, bottom: usize) {
    with_document(|doc| {
        doc.selection = (left < right && top < bottom && right <= doc.width && bottom <= doc.height)
            .then_some([left, top, right, bottom]);
    });
}

#[unsafe(no_mangle)]
pub extern "C" fn image_clear() {
    with_document(|doc| {
        let [left, top, right, bottom] = doc.bounds();
        doc.begin(false);
        for y in top..bottom {
            doc.current.pixels[y * doc.width + left..y * doc.width + right].fill(0);
        }
        doc.commit();
    });
}

#[unsafe(no_mangle)]
pub extern "C" fn image_fill(x: usize, y: usize, color: u32) {
    with_document(|doc| {
        let [left, top, right, bottom] = doc.bounds();
        if x < left || x >= right || y < top || y >= bottom {
            return;
        }
        if doc.current.pixels[y * doc.width + x] == color {
            return;
        }
        doc.begin(false);
        // This remains valid for the duration of the synchronous operation.
        unsafe { fill_region(doc.current.pixels.as_mut_ptr(), doc.width, doc.height,
            x, y, color, left, top, right, bottom) };
        doc.commit();
    });
}

#[unsafe(no_mangle)]
pub extern "C" fn image_line(ax: i32, ay: i32, bx: i32, by: i32, size: i32, mode: u32, color: u32, angle: i32) {
    with_document(|doc| {
        if doc.original.is_none() || (mode == 2 && doc.visited.len() != doc.current.pixels.len())
            || [ax,bx].iter().any(|x| *x < 0 || *x >= doc.width as i32)
            || [ay,by].iter().any(|y| *y < 0 || *y >= doc.height as i32)
        {
            return;
        }
        let dx = (bx - ax).abs();
        let dy = -(by - ay).abs();
        let step_x = if ax < bx { 1 } else { -1 };
        let step_y = if ay < by { 1 } else { -1 };
        let (mut x, mut y) = (ax, ay);
        let mut error = dx + dy;
        loop {
            doc.stamp(x, y, size, mode, color, angle);
            if (x, y) == (bx, by) { break; }
            let twice = error * 2;
            if twice >= dy { error += dy; x += step_x; }
            if twice <= dx { error += dx; y += step_y; }
        }
    });
}

/// # Safety
/// The pixels must reference a live allocation separate from the document.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn image_paste(pixels: *const u32, count: usize) {
    with_document(|doc| {
        let [left, top, right, bottom] = doc.bounds();
        let width = right - left;
        if count != width * (bottom - top) {
            return;
        }
        let data = unsafe { std::slice::from_raw_parts(pixels, count) };
        doc.begin(false);
        for (row, pixels) in (top..bottom).zip(data.chunks_exact(width)) {
            let start = row * doc.width + left;
            doc.current.pixels[start..start + width].copy_from_slice(pixels);
        }
        doc.commit();
    });
}

#[unsafe(no_mangle)]
pub extern "C" fn image_close() {
    DOCUMENT.with_borrow_mut(|value| *value = None);
}
