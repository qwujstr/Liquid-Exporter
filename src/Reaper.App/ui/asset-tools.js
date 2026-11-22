'use strict';

const structureDialog = $('structure-dialog');
let structureAsset, structurePath = '', structureOffset = 0, structureSerial = 0, structureRequest = 0, choiceRequest = 0;
let structureTarget = null, structureBusy = false, choiceTimer;
const thumbnailQueue = [];
let thumbnailActive = 0;
const button = (label, click) => {
    const element = document.createElement('button');
    element.className = 'glass-button';
    element.textContent = label;
    element.onclick = click;
    return element;
};
function structureStatus(message) { $('structure-status').textContent = message; }
function structureLoad(path, offset = 0) {
    structurePath = path;
    structureOffset = offset;
    structureTarget = null;
    thumbnailQueue.length = 0;
    $('structure-picker').hidden = true;
    $('structure-up').disabled = !path;
    $('structure-more').hidden = true;
    $('structure-choices').replaceChildren();
    $('structure-rows').replaceChildren();
    structureStatus('Reading fields…');
    structureRequest = ++structureSerial;
    post({type:'editorStructure', ...structureAsset, path, offset, requestId:structureRequest});
}
$('editor-structure-open').onclick = () => {
    if (!editorSelection || editorBusy) return;
    structureAsset = {fileId:editorFileId, assetId:editorSelection.Id};
    $('structure-title').textContent = editorSelection.Name;
    structureBusy = false;
    animateAssetDialog(structureDialog,true);
    structureLoad('');
};
$('structure-close').onclick = () => { if (!structureBusy) animateAssetDialog(structureDialog,false); };
structureDialog.addEventListener('cancel', event => {
    event.preventDefault();
    if (!structureBusy) animateAssetDialog(structureDialog,false);
});
structureDialog.addEventListener('close', () => { thumbnailQueue.length = 0; clearTimeout(choiceTimer); });
$('structure-up').onclick = () => structureLoad(structurePath.replace(/(?:\/\d+)?\/@$|\/\d+$/, ''));
$('structure-more').onclick = () => structureLoad(structurePath, structureOffset + 60);
$('structure-schemas').onclick = () => structureChange('editorSchemas', '');
function structureChange(type, path, extra = {}) {
    if (structureBusy) return;
    structureBusy = true;
    thumbnailQueue.length = 0;
    structureStatus('Applying…');
    post({type, ...structureAsset, path, ...extra});
}
function nextThumbnail() {
    if (!structureDialog.open || structureBusy || thumbnailActive || !thumbnailQueue.length) return;
    const item = thumbnailQueue.shift();
    if (!item.img.isConnected) { nextThumbnail(); return; }
    thumbnailActive = ++structureSerial;
    thumbnailActiveImage = item.img;
    post({type:'editorThumbnail', ...structureAsset, path:item.path, requestId:thumbnailActive});
}
let thumbnailActiveImage;
function showStructure(page) {
    $('structure-path').textContent = page.Path || 'Root';
    const list = $('structure-rows');
    list.classList.remove('structure-populate');
    if (structureOffset > 0) list.append(button('Previous fields', () => structureLoad(structurePath, Math.max(0, structureOffset-60))));
    for (const row of page.Rows) {
        const element = document.createElement('div');
        element.className = 'structure-row';
        const image = document.createElement('img');
        image.alt = '';
        image.hidden = true;
        if (row.VisualPath) thumbnailQueue.push({img:image,path:row.VisualPath});
        const badge = document.createElement('span');
        badge.className = 'structure-icon';
        badge.textContent = row.VisualType === 43 ? '⬡' : '◇';
        const identity = document.createElement('div');
        const title = document.createElement('strong');
        title.textContent = row.Name;
        const detail = document.createElement('small');
        detail.textContent = row.Type + (row.Children ? ' · ' + row.Children : '') + (row.Scalar || row.Value == null ? '' : ' · ' + row.Value);
        identity.append(title,detail);
        element.append(badge,image,identity);
        if (row.Children) element.append(button('Open', () => structureLoad(row.Path)));
        if (row.Link) element.append(button('Follow', () => structureLoad(row.Path + '/@')));
        if (row.Scalar) {
            const input = document.createElement('input');
            input.value = row.Value ?? '';
            input.setAttribute('aria-label',row.Name);
            element.append(input,button('Apply', () => structureChange('editorSetField',row.Path,{value:input.value})));
        }
        const localVisual = row.VisualPath && !row.VisualPath.slice(row.Path.length).includes('/@');
        if (row.ReferenceType) element.append(button('Choose', () => showChoices(row)));
        else if (row.VisualType && localVisual) element.append(button('Choose preview asset', () =>
            showChoices({ReferenceType:row.VisualType, Path:row.VisualPath})));
        if (row.ReferenceType === 213 && row.Link) element.append(button('Import PNG', () => structureChange('editorAddSprite',row.Path)));
        else if (!row.ReferenceType && row.VisualType === 213 && localVisual)
            element.append(button('Import PNG', () => structureChange('editorAddSprite',row.VisualPath)));
        list.append(element);
    }
    list.classList.add('structure-populate');
    $('structure-more').hidden = page.Total <= structureOffset + page.Rows.length;
    structureStatus(page.Total ? (structureOffset+1) + '–' + (structureOffset+page.Rows.length) + ' / ' + page.Total : 'No fields');
    nextThumbnail();
}
function showChoices(row) {
    structureTarget = row;
    thumbnailQueue.length = 0;
    $('structure-picker').hidden = false;
    $('structure-choices').replaceChildren();
    $('structure-search').value = '';
    $('structure-search').focus();
    findChoices();
}
function findChoices() {
    if (!structureTarget || !structureDialog.open) return;
    choiceRequest = ++structureSerial;
    post({type:'editorFindReference', classId:structureTarget.ReferenceType, query:$('structure-search').value.trim(), requestId:choiceRequest});
}
$('structure-search').oninput = () => {
    clearTimeout(choiceTimer);
    choiceTimer = setTimeout(findChoices,200);
};
bridge?.addEventListener('message',event => {
    const m = event.data;
    if (m.type === 'editorThumbnail' && m.requestId === thumbnailActive) {
        if (m.image && thumbnailActiveImage?.isConnected) {
            thumbnailActiveImage.src = m.image;
            thumbnailActiveImage.hidden = false;
            thumbnailActiveImage.previousElementSibling.hidden = true;
        }
        thumbnailActive = 0;
        nextThumbnail();
    }
    if (!structureDialog.open) return;
    if (m.type === 'editorError') {
        if (structureDialog.open) { structureBusy = false; structureStatus(m.message); }
    }
    if (!structureDialog.open) return;
    if (m.type === 'editorStructure' && m.requestId === structureRequest) showStructure(m.value);
    if (m.type === 'editorStructureIdle') { structureBusy = false; structureStatus('Cancelled'); }
    if (m.type === 'editorStructureChanged') {
        structureBusy = false;
        editorFiles = m.files;
        editorTabs();
        structureLoad(structurePath,structureOffset);
    }
    if (m.type === 'editorReferenceChoices' && m.requestId === choiceRequest && structureTarget) {
        const list = $('structure-choices');
        list.replaceChildren();
        if (!m.choices.length) list.textContent = 'No matching assets';
        for (const choice of m.choices) list.append(button(choice.Name+' · '+choice.File, () =>
            structureChange('editorSetReference',structureTarget.Path,{targetFile:choice.FileId,targetAsset:choice.AssetId})));
        if (m.choices.length === 100) list.append('Refine your search to see other matches.');
    }
});
