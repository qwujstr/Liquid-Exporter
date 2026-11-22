#[unsafe(no_mangle)]
pub extern "C" fn alloc_pixels(count: usize) -> *mut u32 {
    Box::into_raw(vec![0; count].into_boxed_slice()).cast()
}

/// # Safety
/// The pointer and length must belong to an active alloc_pixels allocation.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn free_pixels(ptr: *mut u32, count: usize) {
    unsafe { drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(ptr, count))) };
}

/// # Safety
/// Pixels must reference width * height writable RGBA pixels.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn fill_region(
    pixels: *mut u32,
    width: usize,
    height: usize,
    x: usize,
    y: usize,
    replacement: u32,
    left: usize,
    top: usize,
    right: usize,
    bottom: usize,
) {
    if left >= right || top >= bottom || right > width || bottom > height
        || x < left || x >= right || y < top || y >= bottom
    {
        return;
    }
    let Some(len) = width.checked_mul(height) else { return };
    // The JS caller owns this allocation for the duration of the synchronous call.
    let pixels = unsafe { std::slice::from_raw_parts_mut(pixels, len) };
    let origin = y * width + x;
    let color = pixels[origin];
    if color == replacement {
        return;
    }
    let mut queue = vec![origin];
    pixels[origin] = replacement;
    while let Some(index) = queue.pop() {
        let row = index / width;
        let col = index % width;
        let mut add = |neighbor: usize| {
            if pixels[neighbor] == color {
                pixels[neighbor] = replacement;
                queue.push(neighbor);
            }
        };
        if col > left { add(index - 1); }
        if col + 1 < right { add(index + 1); }
        if row > top { add(index - width); }
        if row + 1 < bottom { add(index + width); }
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn hue_pixel(pixel: u32, degrees: i32) -> u32 {
    let [r, g, b, a] = pixel.to_le_bytes();
    let [r, g, b] = [r, g, b].map(f32::from);
    let max = r.max(g).max(b);
    let min = r.min(g).min(b);
    let delta = max - min;
    if delta == 0.0 || a == 0 { return pixel; }
    let hue = if max == r { (g - b) / delta }
        else if max == g { (b - r) / delta + 2.0 }
        else { (r - g) / delta + 4.0 };
    let hue = (hue + degrees as f32 / 60.0).rem_euclid(6.0);
    let m = delta * (1.0 - (hue.rem_euclid(2.0) - 1.0).abs());
    let rgb = match hue as u32 {
        0 => [delta, m, 0.0], 1 => [m, delta, 0.0], 2 => [0.0, delta, m],
        3 => [0.0, m, delta], 4 => [m, 0.0, delta], _ => [delta, 0.0, m],
    }.map(|value| (value + min).round() as u8);
    u32::from_le_bytes([rgb[0], rgb[1], rgb[2], a])
}
mod document;
