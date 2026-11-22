'use strict';

const assetThumbnailCache = new Map();
const decodingThumbnails = new WeakSet();
let thumbnailRevision = 0, assetThumbSerial = 0, activeAssetThumbs = null, assetThumbTimer;
const assetScroll = $('editor-scroll');

function thumbnailKey(file,asset) { return file+':'+asset; }
function decorateAssetName(cell,asset) {
    if (!['Sprite','Texture2D'].includes(asset.Type)) return;
    const wrapper = document.createElement('div');
    wrapper.className = 'asset-row-name';
    const thumb = document.createElement('span');
    thumb.className = 'asset-thumb';
    thumb.dataset.assetThumb = asset.Id;
    thumb.setAttribute('aria-hidden','true');
    thumb.append(createAssetThumbnailIcon());
    const title = document.createElement('span');
    title.textContent = asset.Name;
    wrapper.append(thumb,title);
    cell.replaceChildren(wrapper);
    applyAssetThumbnail(thumb,assetThumbnailCache.get(thumbnailKey(editorFileId,asset.Id)));
}
function createAssetThumbnailIcon() {
    const icon = document.createElementNS('http://www.w3.org/2000/svg','svg');
    const use = document.createElementNS(icon.namespaceURI,'use');
    use.setAttribute('href','#i-image');
    icon.append(use);
    return icon;
}
function applyAssetThumbnail(element,url) {
    if (!url || element.firstElementChild?.tagName==='IMG' || decodingThumbnails.has(element)) return;
    const revision = thumbnailRevision;
    const image = document.createElement('img');
    image.alt = '';
    decodingThumbnails.add(element);
    image.src = url;
    image.decode().then(() => {
        if (revision === thumbnailRevision && element.isConnected) element.replaceChildren(image);
    }).catch(() => {
        // Keep the placeholder when a thumbnail cannot be decoded.
    }).finally(() => decodingThumbnails.delete(element));
}
function scheduleAssetThumbnails() {
    clearTimeout(assetThumbTimer);
    assetThumbTimer = setTimeout(requestAssetThumbnails,70);
}
function requestAssetThumbnails() {
    if (activeAssetThumbs || !editorFileId || editorBusy || !$('page-editor').classList.contains('active')) return;
    const bounds = assetScroll.getBoundingClientRect();
    const assetIds = [];
    for (const element of $('editor-rows').querySelectorAll('[data-asset-thumb]')) {
        const key = thumbnailKey(editorFileId,element.dataset.assetThumb);
        if (assetThumbnailCache.has(key)) { applyAssetThumbnail(element,assetThumbnailCache.get(key)); continue; }
        const rect = element.getBoundingClientRect();
        if (rect.bottom < bounds.top || rect.top > bounds.bottom) continue;
        assetIds.push(element.dataset.assetThumb);
        if (assetIds.length === 4) break;
    }
    if (!assetIds.length) return;
    activeAssetThumbs = {requestId:++assetThumbSerial,revision:thumbnailRevision,fileId:editorFileId};
    post({type:'editorListThumbnails',requestId:activeAssetThumbs.requestId,fileId:editorFileId,assetIds});
}
function invalidateAssetThumbnails() {
    thumbnailRevision++;
    assetThumbnailCache.clear();
    for (const item of $('editor-rows').querySelectorAll('.asset-thumb img')) {
        item.parentElement.replaceChildren(createAssetThumbnailIcon());
    }
    scheduleAssetThumbnails();
}
bridge?.addEventListener('message',event=>{
    const m = event.data;
    if (['editorOpened','editorChanged','editorStructureChanged','editorSaved'].includes(m.type)) invalidateAssetThumbnails();
    if (!activeAssetThumbs || m.requestId!==activeAssetThumbs.requestId) return;
    if (m.type==='editorListThumbnails') {
        if (activeAssetThumbs.revision===thumbnailRevision) {
            for (const item of m.thumbnails) {
                assetThumbnailCache.set(thumbnailKey(m.fileId,item.assetId),item.image);
                if (assetThumbnailCache.size>320) assetThumbnailCache.delete(assetThumbnailCache.keys().next().value);
            }
        }
    } else if (!(m.type==='editorError' && m.action==='editorListThumbnails')) return;
    activeAssetThumbs = null;
    scheduleAssetThumbnails();
});
new MutationObserver(scheduleAssetThumbnails).observe($('page-editor'),{attributes:true,attributeFilter:['class']});
