'use strict';

const paintDialog = $('paint-dialog');
const viewport = $('paint-viewport');
const canvas = $('paint-canvas');
const pen = canvas.getContext('2d', {willReadFrequently:true});
const paintView = {x:0,y:0,scale:1};
const drawingTools = [
    ['brush','Brush','B','M14 6l4-4 4 4-10 10-4-4 6-6M8 13c-4 0-2 6-6 7 5 2 10 0 10-4'],
    ['pencil','Pencil','P','M3 21l2-6L17 3l4 4L9 19 3 21ZM14 6l4 4'],
    ['hue','Hue brush','U','M12 3c4 6 7 9 7 13a7 7 0 0 1-14 0c0-4 3-7 7-13ZM9 17a3 3 0 0 0 4 2'],
    ['eraser','Eraser','E','M6 19l-4-4a2 2 0 0 1 0-3l8-8a2 2 0 0 1 3 0l7 7a2 2 0 0 1 0 3l-5 5H6ZM6 8l9 9M13 19h9'],
    ['fill','Fill contiguous area','F','M3 12l8-8 8 8-7 7a2 2 0 0 1-3 0l-6-6ZM3 12h16M7 2l8 8M21 14c0 0-3 4-3 6a3 3 0 0 0 6 0c0-2-3-6-3-6Z'],
    ['picker','Eyedropper','I','M15 4l5 5M13 6l5 5-9 9H4v-5l9-9ZM16 4l2-2 4 4-2 2'],
    ['line','Line','L','M4 20L20 4M3 18v3h3M18 3h3v3'],
    ['rectangle','Rectangle outline','R','M4 5h16v14H4z'],
    ['select','Rectangle select','M','M3 9V4h5m4 0h4m4 0h1v5m0 4v5m-1 2h-5m-4 0H6m-3 0v-5'],
    ['pan','Hand','H','M8 12V6a2 2 0 0 1 4 0v4-6a2 2 0 0 1 4 0v6-4a2 2 0 0 1 4 0v10c0 4-2 6-6 6h-2c-2 0-4-2-5-4L3 12a2 2 0 0 1 3-2l2 2Z']
];
let paintTool = 'brush', paintAsset, paintDrag, paintPending = false, paintSession = 0;
let paintSelection = null, paintSpace = false, paintPointer = null, paintRenderFrame = 0;
let pixelKernel = null;
const kernelReady = fetch('pixel-kernel.wasm').then(response => {
    if (!response.ok) throw Error('Pixel engine unavailable');
    return response.arrayBuffer();
}).then(bytes => WebAssembly.instantiate(bytes))
    .then(module => { pixelKernel = module.instance.exports; syncPaintTools(); })
    .catch(() => syncPaintTools());

for (const [id,name,key,path] of drawingTools) {
    const element = document.createElement('button');
    element.type = 'button';
    element.dataset.tool = id;
    element.title = name + ' (' + key + ')';
    element.setAttribute('aria-label', element.title);
    const icon = document.createElementNS('http://www.w3.org/2000/svg','svg');
    icon.setAttribute('viewBox','0 0 24 24');
    icon.setAttribute('aria-hidden','true');
    const stroke = document.createElementNS(icon.namespaceURI,'path');
    stroke.setAttribute('d',path);
    icon.append(stroke);
    element.append(icon);
    element.onclick = () => { setPaintTool(id); viewport.focus({preventScroll:true}); };
    $('paint-tools').append(element);
}
function syncPaintTools() {
    for (const element of $('paint-tools').children) {
        element.setAttribute('aria-pressed', String(element.dataset.tool === paintTool));
        element.disabled = !pixelKernel && ['hue','fill'].includes(element.dataset.tool);
    }
    $('paint-hue-option').hidden = paintTool !== 'hue';
    $('paint-size-option').hidden = !['brush','hue','eraser','line','rectangle'].includes(paintTool);
}
function setPaintTool(id) {
    if (paintDrag || paintPending || (!pixelKernel && ['hue','fill'].includes(id))) return;
    paintTool = id;
    syncPaintTools();
    updatePaintOverlays();
}
syncPaintTools();

function animateAssetDialog(dialog, opening) {
    if (!opening && (!dialog.open || dialog.classList.contains('dialog-closing'))) return;
    dialog.getAnimations().forEach(animation => animation.cancel());
    if (opening) dialog.showModal();
    const frames = [{opacity:0,transform:'translateY(9px) scale(.986)'},{opacity:1,transform:'translateY(0) scale(1)'}];
    if (!opening) {
        dialog.classList.add('dialog-closing');
    }
    if (reducedMotion()) {
        if (!opening) { dialog.close(); dialog.classList.remove('dialog-closing'); }
        return;
    }
    const transition = dialog.animate(opening ? frames : frames.reverse(), {duration:opening?180:145,easing:'ease-out',fill:'both'});
    transition.finished.then(() => {
        if (!opening) {
            dialog.close();
            dialog.classList.remove('dialog-closing');
        }
        transition.cancel();
    }).catch(() => {});
}

function paintButtons() {
    const state = pixelKernel?.image_state() ?? 0;
    $('paint-undo').disabled = !(state & 1) || paintPending || !!paintDrag;
    $('paint-redo').disabled = !(state & 2) || paintPending || !!paintDrag;
    $('paint-apply').disabled = !(state & 4) || paintPending || !!paintDrag;
    $('paint-close').disabled = paintPending;
}
function paintRender() {
    cancelAnimationFrame(paintRenderFrame);
    paintRenderFrame = 0;
    const ptr = pixelKernel?.image_pixels();
    if (!ptr) return;
    const pixels = new Uint8ClampedArray(pixelKernel.memory.buffer,ptr,canvas.width*canvas.height*4);
    pen.putImageData(new ImageData(pixels,canvas.width,canvas.height),0,0);
}
function queuePaintRender() {
    if (!paintRenderFrame) paintRenderFrame = requestAnimationFrame(paintRender);
}
function setPaintSelection(box) {
    paintSelection = box;
    pixelKernel.image_select(box?.left ?? 0,box?.top ?? 0,box?.right ?? 0,box?.bottom ?? 0);
}
function paintTransform() {
    const {x,y,scale} = paintView;
    const plane = $('paint-plane');
    plane.style.left = x+'px';
    plane.style.top = y+'px';
    plane.style.width = canvas.width*scale+'px';
    plane.style.height = canvas.height*scale+'px';
    plane.style.setProperty('--pixel-step',scale+'px');
    $('paint-pixels').hidden = !$('paint-grid').checked || scale < 8;
    updatePaintOverlays();
}
function paintStatus(point) {
    $('paint-status').textContent = canvas.width+' × '+canvas.height+' px  ·  '+Math.round(paintView.scale*100)+'%'
        + (point && point.inside ? '  ·  '+point.x+', '+point.y : '');
}
function paintCenter(scale) {
    paintView.scale = Math.min(64,Math.max(.01,scale));
    paintView.x = (viewport.clientWidth-canvas.width*paintView.scale)/2;
    paintView.y = (viewport.clientHeight-canvas.height*paintView.scale)/2;
    paintTransform();
}
function paintFit() {
    paintCenter(Math.min(24, (viewport.clientWidth-56)/canvas.width, (viewport.clientHeight-56)/canvas.height));
}
function paintZoom(scale,x,y) {
    const next = Math.min(64,Math.max(.01,scale));
    const ratio = next/paintView.scale;
    paintView.x = x-(x-paintView.x)*ratio;
    paintView.y = y-(y-paintView.y)*ratio;
    paintView.scale = next;
    paintTransform();
}
function paintPoint(pointer) {
    const x = Math.floor((pointer.x-paintView.x)/paintView.scale);
    const y = Math.floor((pointer.y-paintView.y)/paintView.scale);
    return {x,y,inside:x>=0&&y>=0&&x<canvas.width&&y<canvas.height};
}
function paintBounds(a,b) {
    const clamp = (value,size) => Math.max(0,Math.min(size-1,value));
    const ax = clamp(a.x,canvas.width), bx = clamp(b.x,canvas.width);
    const ay = clamp(a.y,canvas.height), by = clamp(b.y,canvas.height);
    return {left:Math.min(ax,bx),right:Math.max(ax,bx)+1,top:Math.min(ay,by),bottom:Math.max(ay,by)+1};
}
function updatePaintOverlays() {
    const border = $('paint-selection');
    border.hidden = !paintSelection;
    if (paintSelection) showPaintBox(border,paintSelection.left,paintSelection.top,
        paintSelection.right-paintSelection.left,paintSelection.bottom-paintSelection.top);
    const cursor = $('paint-cursor');
    const tool = paintSpace ? 'pan' : paintDrag?.tool ?? paintTool;
    const point = paintPointer && paintPoint(paintPointer);
    const isStamp = ['brush','pencil','hue','eraser','line','rectangle'].includes(tool);
    cursor.hidden = !point?.inside || !isStamp || viewport.dataset.pointerOutside === 'true';
    viewport.dataset.tool = tool;
    viewport.classList.toggle('paint-panning',paintDrag?.tool === 'pan');
    viewport.classList.toggle('paint-precision',!cursor.hidden);
    if (!cursor.hidden) {
        const size = tool === 'pencil' ? 1 : Number($('paint-size').value);
        cursor.classList.toggle('round',size>1);
        const inset = Math.floor((size-1)/2);
        showPaintBox(cursor,point.x-inset,point.y-inset,size,size);
    }
    paintStatus(point);
}
function showPaintBox(element,x,y,width,height) {
    const scale = paintView.scale;
    element.style.left = (paintView.x+x*scale)+'px';
    element.style.top = (paintView.y+y*scale)+'px';
    element.style.width = Math.max(2,width*scale)+'px';
    element.style.height = Math.max(2,height*scale)+'px';
}

$('editor-paint-open').onclick = () => {
    const img = $('editor-image');
    if (paintDialog.open || editorBusy || !img.naturalWidth || !editorSelection) return;
    if (!pixelKernel) { toast('The Rust image editor could not be loaded.'); return; }
    if (img.naturalWidth*img.naturalHeight > 16_777_216) { toast('Use Replace for images larger than 16 megapixels.'); return; }
    paintSession++;
    paintAsset = {fileId:editorFileId,assetId:editorSelection.Id};
    paintSelection = paintDrag = paintPointer = null;
    paintPending = paintSpace = false;
    canvas.width = img.naturalWidth;
    canvas.height = img.naturalHeight;
    pen.drawImage(img,0,0);
    const pixels = pen.getImageData(0,0,canvas.width,canvas.height).data;
    const ptr = pixelKernel.image_open(canvas.width,canvas.height);
    if (!ptr) { toast('The image could not be opened.'); return; }
    new Uint8ClampedArray(pixelKernel.memory.buffer,ptr,pixels.length).set(pixels);
    animateAssetDialog(paintDialog,true);
    viewport.focus();
    viewport.dataset.pointerOutside = 'true';
    paintFit();
    paintButtons();
};
window.liquidEditor.unappliedImage = () => paintDialog.open && !paintPending && !!(pixelKernel?.image_state() & 4);
function closePaint() {
    if (paintPending || paintDrag) return;
    if (!(pixelKernel.image_state() & 4) || confirm('Discard unapplied image edits?')) animateAssetDialog(paintDialog,false);
}
$('paint-close').onclick = closePaint;
paintDialog.addEventListener('cancel',event => { event.preventDefault(); closePaint(); });
paintDialog.addEventListener('close',() => {
    cancelAnimationFrame(paintRenderFrame);
    paintRenderFrame = 0;
    pixelKernel?.image_close();
});
$('paint-fit').onclick = paintFit;
$('paint-actual').onclick = () => paintCenter(1);
$('paint-grid').onchange = paintTransform;
$('paint-size').oninput = () => { $('paint-size-value').textContent = $('paint-size').value+' px'; updatePaintOverlays(); };
$('paint-hue').oninput = () => { $('paint-hue-value').textContent = $('paint-hue').value+'°'; };
$('paint-undo').onclick = () => { pixelKernel.image_undo(); paintRender(); paintButtons(); };
$('paint-redo').onclick = () => { pixelKernel.image_redo(); paintRender(); paintButtons(); };
$('paint-paste').onclick = () => { if (!paintPending && !paintDrag) post({type:'editorImagePaste',requestId:paintSession}); };
$('paint-apply').onclick = () => {
    if ($('paint-apply').disabled) return;
    paintRender();
    const png = canvas.toDataURL('image/png').split(',')[1];
    paintPending = true;
    paintButtons();
    post({type:'editorImageApply', ...paintAsset, png});
};

async function paintPaste(url) {
    const session = paintSession;
    const img = new Image();
    img.src = url;
    try { await img.decode(); }
    catch { $('paint-status').textContent = 'Clipboard image could not be decoded.'; return; }
    if (!paintDialog.open || paintSession !== session || paintPending || paintDrag) return;
    const bounds = paintSelection ?? {left:0,top:0,right:canvas.width,bottom:canvas.height};
    const width = bounds.right-bounds.left, height = bounds.bottom-bounds.top;
    if ((img.width!==width || img.height!==height) && !confirm('Resize pasted image to '+width+' × '+height+'?')) return;
    const bitmap = new OffscreenCanvas(width,height);
    const context = bitmap.getContext('2d');
    context.imageSmoothingEnabled = false;
    context.drawImage(img,0,0,width,height);
    const pixels = context.getImageData(0,0,width,height).data;
    const length = width*height, ptr = pixelKernel.alloc_pixels(length);
    try {
        new Uint8ClampedArray(pixelKernel.memory.buffer,ptr,pixels.length).set(pixels);
        pixelKernel.image_paste(ptr,length);
    } finally { pixelKernel.free_pixels(ptr,length); }
    paintRender();
    paintButtons();
    $('paint-status').textContent = 'Image pasted';
}
paintDialog.addEventListener('paste',event => {
    event.preventDefault();
    const file = [...event.clipboardData.files].find(item => item.type.startsWith('image/'));
    if (!file) { $('paint-paste').click(); return; }
    const session = paintSession;
    const reader = new FileReader();
    reader.onload = () => { if (session === paintSession) paintPaste(reader.result); };
    reader.readAsDataURL(file);
});

function packedColor(hex) {
    return (parseInt(hex.slice(1,3),16) | parseInt(hex.slice(3,5),16)<<8 | parseInt(hex.slice(5,7),16)<<16 | 0xff000000) >>> 0;
}
function paintFill(point,color) {
    pixelKernel.image_fill(point.x,point.y,packedColor(color));
    paintRender();
    paintButtons();
}
function paintPick(point) {
    const pixel = pixelKernel.image_pick(point.x,point.y) >>> 0;
    $('paint-color').value = '#'+[0,8,16].map(bits=>((pixel>>>bits)&255).toString(16).padStart(2,'0')).join('');
}
function paintLine(a,b,stroke) {
    const mode = stroke.tool==='eraser' ? 1 : stroke.tool==='hue' ? 2 : 0;
    pixelKernel.image_line(a.x,a.y,b.x,b.y,stroke.size,mode,packedColor(stroke.color),stroke.angle);
}
function pointInViewport(event) {
    const rect = viewport.getBoundingClientRect();
    return {x:event.clientX-rect.left-viewport.clientLeft,y:event.clientY-rect.top-viewport.clientTop};
}
function limitPoint(point) {
    return {...point,x:Math.max(0,Math.min(canvas.width-1,point.x)),y:Math.max(0,Math.min(canvas.height-1,point.y))};
}
viewport.addEventListener('pointerdown',event => {
    if (paintPending || paintDrag || ![0,1].includes(event.button)) return;
    paintPointer = pointInViewport(event);
    const point = paintPoint(paintPointer);
    const tool = event.button===1 || paintSpace ? 'pan' : event.altKey ? 'picker' : paintTool;
    if (tool !== 'pan' && !point.inside) return;
    event.preventDefault();
    viewport.focus({preventScroll:true});
    if (tool === 'picker') { paintPick(point); return; }
    if (tool === 'fill') { paintFill(point,$('paint-color').value); return; }
    const shape = tool==='line'||tool==='rectangle';
    const editing = tool!=='pan' && tool!=='select';
    if (editing) pixelKernel.image_open_edit(tool==='hue');
    paintDrag = {id:event.pointerId,tool,start:point,last:point,pointer:paintPointer,shape,editing,
        previousSelection:paintSelection,size:tool==='pencil'?1:Number($('paint-size').value),
        color:$('paint-color').value,angle:Number($('paint-hue').value)};
    viewport.setPointerCapture(event.pointerId);
    if (tool === 'select') setPaintSelection(paintBounds(point,point));
    else if (editing) { paintLine(point,point,paintDrag); queuePaintRender(); }
    updatePaintOverlays();
    paintButtons();
});
viewport.addEventListener('pointermove',event => {
    paintPointer = pointInViewport(event);
    viewport.dataset.pointerOutside = String(paintPointer.x<0||paintPointer.y<0||paintPointer.x>=viewport.clientWidth||paintPointer.y>=viewport.clientHeight);
    const drag = paintDrag;
    if (drag?.id !== event.pointerId) { updatePaintOverlays(); return; }
    if (drag.tool === 'pan') {
        paintView.x += paintPointer.x-drag.pointer.x;
        paintView.y += paintPointer.y-drag.pointer.y;
        drag.pointer = paintPointer;
        paintTransform();
        return;
    }
    const point = limitPoint(paintPoint(paintPointer));
    if (drag.tool === 'select') setPaintSelection(paintBounds(drag.start,point));
    else if (drag.shape) {
        pixelKernel.image_reset_preview();
        if (drag.tool==='line') paintLine(drag.start,point,drag);
        else {
            const a = drag.start, b = {x:point.x,y:a.y}, c = point, d = {x:a.x,y:c.y};
            paintLine(a,b,drag); paintLine(b,c,drag); paintLine(c,d,drag); paintLine(d,a,drag);
        }
    } else paintLine(drag.last,point,drag);
    if (drag.editing) queuePaintRender();
    drag.last = point;
    updatePaintOverlays();
});
for (const type of ['pointerup','pointercancel','lostpointercapture']) viewport.addEventListener(type,event => {
    if (paintDrag?.id !== event.pointerId) return;
    if (paintDrag.editing) {
        if (type!=='pointerup' && paintDrag.shape) pixelKernel.image_abort();
        else pixelKernel.image_commit();
        paintRender();
    }
    if (type!=='pointerup' && paintDrag.tool==='select') setPaintSelection(paintDrag.previousSelection);
    paintDrag = null;
    updatePaintOverlays();
    paintButtons();
});
viewport.addEventListener('pointerleave',() => {
    viewport.dataset.pointerOutside = 'true';
    paintPointer = null;
    updatePaintOverlays();
});
viewport.addEventListener('pointerenter',() => { viewport.dataset.pointerOutside = 'false'; });
viewport.addEventListener('wheel',event => {
    event.preventDefault();
    if (paintDrag) return;
    paintPointer = pointInViewport(event);
    const delta = event.deltaY*(event.deltaMode===1?16:event.deltaMode===2?viewport.clientHeight:1);
    paintZoom(paintView.scale*Math.exp(-Math.max(-400,Math.min(400,delta))*.002),paintPointer.x,paintPointer.y);
},{passive:false});
paintDialog.addEventListener('keydown',event => {
    const key = event.key.toLowerCase(), ctrl = event.ctrlKey||event.metaKey;
    if (!ctrl && (event.target.matches('input') || event.target.matches('button') && [' ','enter'].includes(key))) return;
    if (ctrl && ['z','y'].includes(key)) {
        event.preventDefault(); $(event.shiftKey||key==='y'?'paint-redo':'paint-undo').click();
    } else if (ctrl && key==='d') {
        event.preventDefault(); setPaintSelection(null); updatePaintOverlays();
    } else if (!ctrl && event.code==='Space') {
        event.preventDefault(); paintSpace = true; updatePaintOverlays();
    } else if (!ctrl && !paintDrag && !paintPending) {
        const tool = drawingTools.find(([, , hotkey])=>hotkey.toLowerCase()===key);
        if (tool) { event.preventDefault(); setPaintTool(tool[0]); }
        if (key==='0') paintFit();
        if (key==='1') paintCenter(1);
        if (key==='delete' && paintSelection) {
            event.preventDefault();
            pixelKernel.image_clear();
            paintRender();
            paintButtons();
        }
    }
});
window.addEventListener('keyup',event => {
    if (event.code==='Space' && paintSpace) { paintSpace = false; updatePaintOverlays(); }
});
window.addEventListener('blur',() => {
    if (paintSpace) { paintSpace=false; updatePaintOverlays(); }
});
bridge?.addEventListener('message',event => {
    const m=event.data;
    if (!paintDialog.open) return;
    if (m.type==='editorClipboardImage' && m.requestId===paintSession) paintPaste('data:image/png;base64,'+m.png);
    if (m.type==='editorChanged' && paintPending) {
        paintPending = false;
        pixelKernel.image_close();
        animateAssetDialog(paintDialog,false);
    }
    if (m.type==='editorError' && ['editorImagePaste','editorImageApply'].includes(m.action)) {
        paintPending = false;
        $('paint-status').textContent = m.message;
        paintButtons();
    }
});
