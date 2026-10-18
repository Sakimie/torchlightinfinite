'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const escape = value => String(value ?? '').replace(/[&<>"']/g, char => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[char]));
  const clone = value => JSON.parse(JSON.stringify(value));
  const beastNames = {mammoth:'巨象',lion:'狂狮',wolf:'头狼',rat:'硕鼠'};
  const tierNames = ['普通','魔法','稀有','传奇','至臻','绮彩'];
  const phaseNames = {patrol:'选择巡逻目的地',manage:'管理牢房',chain:'连吃 · 选择目标',ratway:'鼠道 · 选择牢房',event:'处理事件',office:'审讯室',tool:'选择刑具',gift:'选择恩赐'};
  const positions = [[1,1],[1,2],[1,3],[1,4],[1,5],[1,6],[2,6],[3,6],[4,6],[4,5],[4,4],[4,3],[4,2],[4,1],[3,1],[2,1]];
  let board = null, boardRevision = 0, shotRevision = 0, scanBusy = false, captureBusy = false, recommendSequence = 0, timer = null, toastTimer = null, editCell = 0, sourceType = '', lastResult = null;
  let bootstrapData = {tools:[],events:[],gifts:[],policy:{},limitations:[]};
  let alwaysOnTop = false, compact = false;
  const emptyRecommendation = $('recommendation').innerHTML;
  let serviceReady=false, initializing=false, visionReady=false, visionBusy=false, visionSequence=0;
  let currentImageData=null, currentImageDimensions=null, imageRevision=0, calibration=null, calibrationDraft=[], calibrationImage=null, calibrationImageRevision=0;

  async function request(path, body) {
    const response = await fetch(`/api/${path}`, body === undefined ? {} : {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
    const data = await response.json().catch(() => ({}));
    if (!response.ok) throw new Error(data.error || data.message || `本地服务返回 ${response.status}`);
    return data;
  }
  const api = window.torchlight || {
    bootstrap:() => request('bootstrap'),demo:() => request('demo'),scan:(dataUrl,previousBoard) => request('scan',{dataUrl,previousBoard}),recommend:(state,options) => request('recommend',{board:state,options}),
    sources:async() => [],capture:async() => {throw new Error('窗口截图需要使用 Windows 桌面应用；浏览器中可导入或粘贴截图。');},
    setAlwaysOnTop:async() => {throw new Error('窗口置顶仅适用于 Windows 桌面应用。');},setCompact:async() => {},
    newBoard:()=>request('new-board'),visionProfile:()=>request('vision-profile'),learnVision:data=>request('learn-vision',data),importVision:profile=>request('import-vision',{profile}),resetVision:()=>request('reset-vision',{})
  };

  function toast(message) {$('toast').textContent = message; $('toast').hidden = false; clearTimeout(toastTimer); toastTimer = setTimeout(() => {$('toast').hidden = true;},6000);}
  function status(message) {$('capture-status').textContent = message;}
  function errorMessage(error) {return error?.message || String(error);}
  function normalizeBoard(state) {
    const base = {...state};
    base.totalDays = 20;
    base.day = Math.max(1,Math.min(20,Number(base.day) || 1));
    base.position = Math.max(0,Math.min(15,Number(base.position) || 0));
    base.phase = phaseNames[base.phase] ? base.phase : 'patrol';
    base.confirmed = base.confirmed !== false;
    base.usurpStage = base.usurpStage ?? 'unknown';
    base.resumePhase = base.resumePhase || null;
    base.destinations = Array.isArray(base.destinations) ? base.destinations.filter(n => Number.isInteger(n) && n >= 0 && n < 16) : [];
    base.tools = Array.isArray(base.tools) ? base.tools : [];
    base.offerIds = Array.isArray(base.offerIds) ? base.offerIds : [];
    base.cells = Array.from({length:16},(_,n) => ({n,kind:n === 0?'office':'locked',tier:0,event:false,ivory:false,scratch:false,...(state.cells || []).find(cell => cell.n === n),n}));
    return base;
  }
  function setBoard(state,type) {board = normalizeBoard(state); boardRevision++;$('vision-confirm').checked=false; sourceType = type; renderBoard(); syncControls(); markStale();updateVisionControls();}
  function setSourceBadge() {const badge=$('board-source');badge.className=`source-badge ${sourceType==='示例局面'?'demo':sourceType==='截图识别'?'scan':''}`;badge.textContent=sourceType || '尚未加载';}
  function markStale() {
    $('recommendation').classList.toggle('stale',!!lastResult);
    if (lastResult) {$('recommend-status').className='status-tag stale';$('recommend-status').textContent='局面已更新 · 请重算';}
    else {$('recommend-status').className='status-tag';$('recommend-status').textContent=board?(board.confirmed?'待比较':'请核对棋盘'):'等待局面';}
    if(board && !board.confirmed) {$('recommend-button').textContent='已核对棋盘，计算建议';$('recommend-status').className='status-tag stale';$('recommend-status').textContent='识别结果待确认';}
  }
  function changed() {boardRevision++;$('vision-confirm').checked=false;sourceType=sourceType==='示例局面'?'示例 · 已修改':'手动确认';renderBoard();setSourceBadge();markStale();updateVisionControls();}
  function resetContext(message='局面已清空 · 请获取新一局截图') {
    board=null;lastResult=null;sourceType='';boardRevision++;shotRevision++;recommendSequence++;
    clearInterval(timer);timer=null;$('auto-input').checked=false;
    $('screenshot-preview').hidden=true;$('screenshot-preview').removeAttribute('src');$('screenshot-area').querySelector('.empty-preview').hidden=false;
    currentImageData=null;currentImageDimensions=null;imageRevision++;$('vision-confirm').checked=false;
    if($('calibration-dialog').open)$('calibration-dialog').close();calibrationImage=null;calibrationDraft=[];
    $('recommendation').innerHTML=emptyRecommendation;$('recommendation').classList.remove('stale');
    $('recommend-button').textContent='比较下一步';
    $('day-input').value=1;$('phase-input').value='patrol';$('destinations-input').value='';
    for(const id of ['tools-input','offers-input'])for(const option of $(id).options)option.selected=false;
    $('objective-input').value='other';$('event-input').value='';$('progress-input').value=0;$('vault-input').value=0;
    $('usurp-stage').value='unknown';$('resume-phase').value='';
    for(const id of ['milestone-input','used-action-input','no-swap-input','no-devour-input','chain-armed-input'])$(id).checked=false;
    $('board-day').textContent='等待新局面';
    if($('cell-dialog').open)$('cell-dialog').close();
    renderBoard();renderIssues();setSourceBadge();markStale();setBusyButtons();status(message);
  }
  function renderBoard() {
    if (!board) {$('board').innerHTML='<div class="board-center"><span class="center-emblem">♜</span><strong>尚未载入局面</strong><span>可先加载示例体验</span></div>';return;}
    $('board-day').textContent=`第 ${board.day} / 20 天 · ${phaseNames[board.phase]}`;
    const captured=board.vault||0;
    const inmates=board.cells.filter(cell=>cell.kind==='inmate').length;
    $('board').innerHTML=board.cells.map(cell=>{
      const [row,col]=positions[cell.n], inmate=cell.kind==='inmate',names={office:'审讯室',locked:'未开启',empty:'空房',nest:'巢穴'};
      const name=inmate?(beastNames[cell.beast]||'待确认'):(names[cell.kind]||'待确认');
      const tier=inmate?(tierNames[cell.tier]||'待确认'):(cell.n===board.position?'当前位置':'');
      const title=`${cell.n+1} 号 · ${name}${inmate?` · ${tier}`:''}${cell.event?' · 有事件':''}${cell.n===board.position?' · 当前所在':''}`;
      return `<button class="cell kind-${escape(cell.kind)} ${inmate?`level-${Number(cell.tier)}`:''} ${cell.n===board.position?'current':''} ${board.destinations.includes(cell.n)?'destination':''}" style="grid-row:${row};grid-column:${col}" data-cell="${cell.n}" title="${escape(title)}" aria-label="${escape(title)}"><span class="cell-number">${String(cell.n+1).padStart(2,'0')}</span>${cell.event?'<span class="event-mark">◆</span>':''}<span class="beast">${escape(name)}</span><span class="cell-tier">${escape(tier)}</span>${cell.ivory||cell.scratch?`<span class="mark">${cell.ivory?'象牙 ':''}${cell.scratch?'抓痕':''}</span>`:''}</button>`;
    }).join('')+`<button class="board-center" id="position-button" title="修改当前位置"><span class="center-emblem">♜</span><strong>${captured} 已捕获绮彩 <span style="color:#576651;padding:0 6px">/</span> ${inmates} 囚犯</strong><span>当前位置 · ${board.position+1} 号</span><span>${sourceType.startsWith('示例')?'示例数据 · 非游戏实况':'点击牢房核对 / 编辑'}</span></button>`;
    $('board').querySelectorAll('[data-cell]').forEach(button=>button.addEventListener('click',()=>openCell(Number(button.dataset.cell))));
    $('position-button').addEventListener('click',()=>openCell(board.position));
    setSourceBadge();
  }
  function syncControls() {
    if (!board) return;
    $('day-input').value=board.day;$('phase-input').value=board.phase;
    $('destinations-input').value=board.destinations.map(n=>n+1).join(', ');
    for(const option of $('tools-input').options) option.selected=board.tools.includes(option.value);
    $('objective-input').value=board.objective==='incident'?'incidents':board.objective || 'other';$('progress-input').value=board.objectiveProgress || 0;$('vault-input').value=board.vault || 0;
    $('milestone-input').checked=!!board.milestone;$('used-action-input').checked=!!board.usedAction;$('event-input').value=board.eventKind || '';
    $('no-swap-input').checked=!!board.noSwap;$('no-devour-input').checked=!!board.noDevour;$('chain-armed-input').checked=!!board.chainArmed;
    $('usurp-stage').value=String(board.usurpStage??'unknown');$('resume-phase').value=board.resumePhase||'';
    for(const option of $('offers-input').options) option.selected=board.offerIds.includes(option.value);
  }
  function renderIssues(issues=[]) {
    $('issues').innerHTML=issues.map(issue=>`<div class="issue">⚑ ${escape(issue.message || issue)}${Number.isInteger(issue.cell)?`<button data-issue-cell="${issue.cell}">核对 ${issue.cell+1} 号</button>`:''}</div>`).join('');
    $('issues').querySelectorAll('[data-issue-cell]').forEach(button=>button.addEventListener('click',()=>openCell(Number(button.dataset.issueCell))));
  }
  function markReadFailure() {
    if(!board)return;
    board.confirmed=false;
    board.needsReview=[...new Set([...(board.needsReview||[]),'新画面未读取成功，当前保留上次局面；请重新截图，或核对后再计算。'])];
    renderIssues(board.needsReview.map(message=>({message})));markStale();
  }
  function setBusyButtons() {$('capture-button').disabled=scanBusy||captureBusy||visionBusy;$('image-input').disabled=scanBusy||captureBusy||visionBusy;$('recommend-button').disabled=scanBusy||captureBusy||visionBusy||!serviceReady;updateVisionControls();}
  async function refreshSources() {
    try {const sources=await api.sources();const selected=$('source-input').value;$('source-input').innerHTML='<option value="">选择游戏窗口或屏幕</option>'+sources.map(source=>`<option value="${escape(source.id)}">${escape(source.name)}</option>`).join('');if(sources.some(source=>source.id===selected))$('source-input').value=selected;else if(sources.length===1)$('source-input').value=sources[0].id;if(selected&&selected!==$('source-input').value)resetContext('原截图窗口已变化，旧局面已清空。');if(!sources.length)status(window.torchlight?'未找到可截图窗口，请刷新或导入图片。':'浏览器预览 · 请导入或粘贴截图');}
    catch(error) {status(`窗口列表暂不可用：${errorMessage(error)}`);}
  }
  function showImage(dataUrl) {
    if(currentImageData!==dataUrl){currentImageData=dataUrl;currentImageDimensions=null;imageRevision++;$('vision-confirm').checked=false;}
    const image=$('screenshot-preview'),revision=imageRevision;
    image.onload=()=>{if(revision!==imageRevision)return;currentImageDimensions={width:image.naturalWidth,height:image.naturalHeight};updateVisionControls();};
    image.src=dataUrl;image.hidden=false;$('screenshot-area').querySelector('.empty-preview').hidden=true;updateVisionControls();
  }
  async function analyzeImage(dataUrl, imageLabel='截图') {
    if(scanBusy||visionBusy){toast('正在处理，请等待本次完成。');return;}
    boardRevision++;
    markStale();
    const currentShot=++shotRevision, revision=boardRevision;
    showImage(dataUrl);
    if(!serviceReady){status(`${imageLabel}已保留 · 本地服务尚未就绪，请重试连接。`);return;}
    if(!visionReady){status(`${imageLabel}已保留 · 首次需校准位置、手工录入并学习样本，再重新识别。`);return;}
    scanBusy=true;setBusyButtons();status(`${imageLabel}已载入 · 正在离线识别…`);
    try {
      const result=await api.scan(dataUrl,['screenshot','manual'].includes(board?.source)?clone(board):null);
      if(currentShot!==shotRevision||revision!==boardRevision){status('识别期间局面已修改，本次旧结果未覆盖。');return;}
      if(!result.board)throw new Error(result.recognition?.summary||'未得到可用局面，请手动核对截图。');
      setBoard(result.board,'截图识别');const reviewIssues=[...(result.issues||[])];for(const message of result.board.needsReview||[])if(!reviewIssues.some(issue=>issue.message===message))reviewIssues.push({message});renderIssues(reviewIssues);
      const recognition=result.recognition||{};
      status(`${recognition.ok&&board.confirmed?'识别完成':'识别待确认'}${recognition.ms?` · ${(recognition.ms/1000).toFixed(1)} 秒`:''}${recognition.summary?` · ${recognition.summary}`:''}`);
      if(!recognition.ok||(result.issues||[]).length)toast('画面已读取，请先核对标出的信息，再参考建议。');
      if(board.confirmed) await recommend(false,true);
    } catch(error){if(currentShot===shotRevision&&revision===boardRevision){markReadFailure();status(`识别失败：${errorMessage(error)}`);toast(errorMessage(error));}}
    finally{scanBusy=false;setBusyButtons();}
  }
  async function capture() {
    if(captureBusy||scanBusy||visionBusy)return;
    if(!$('source-input').value){toast(window.torchlight?'请先选择游戏窗口或屏幕。':'浏览器中请使用「导入截图」，或打开 Windows 桌面应用。');return;}
    boardRevision++;markStale();
    const revision=boardRevision;
    captureBusy=true;setBusyButtons();status('正在截取选中窗口…');
    try{const result=await api.capture($('source-input').value);if(!result?.dataUrl)throw new Error('未取得画面，请重新选择窗口。');if(revision!==boardRevision){status('截图期间局面已修改，本次旧画面未覆盖。');return;}await analyzeImage(result.dataUrl,'窗口截图');}
    catch(error){if(revision===boardRevision){markReadFailure();status(`截图失败：${errorMessage(error)}`);toast(errorMessage(error));if($('auto-input').checked){$('auto-input').checked=false;clearInterval(timer);timer=null;}}}
    finally{captureBusy=false;setBusyButtons();}
  }
  async function normalizeImage(file) {
    if(!file||!file.type.startsWith('image/'))throw new Error('请选择图片文件。');
    if(file.size>25*1024*1024)throw new Error('图片过大，请使用小于 25 MB 的游戏截图。');
    const url=URL.createObjectURL(file);
    try{const img=new Image();img.src=url;await img.decode();const scale=Math.min(1,2560/Math.max(img.naturalWidth,img.naturalHeight));const canvas=document.createElement('canvas');canvas.width=Math.round(img.naturalWidth*scale);canvas.height=Math.round(img.naturalHeight*scale);const context=canvas.getContext('2d');context.drawImage(img,0,0,canvas.width,canvas.height);return canvas.toDataURL('image/png');}
    finally{URL.revokeObjectURL(url);}
  }
  async function importImage(file){if(scanBusy||captureBusy||visionBusy){toast('正在处理画面，请等待本次完成。');return;}const revision=boardRevision;try{const dataUrl=await normalizeImage(file);if(revision!==boardRevision)return;await analyzeImage(dataUrl,'导入图片');}catch(error){toast(errorMessage(error));}}
  function configureAuto() {
    clearInterval(timer);timer=null;
    if(!$('auto-input').checked){status('定时识别已关闭');return;}
    if(!serviceReady||!visionReady){$('auto-input').checked=false;toast('请先连接本地服务并完成首次离线识图学习。');return;}
    if(!$('source-input').value){$('auto-input').checked=false;toast('请先选择游戏窗口；定时识别仅适用于桌面应用。');return;}
    const seconds=Math.max(3,Number($('interval-input').value)||5);
    timer=setInterval(()=>{if(!scanBusy&&!captureBusy&&!document.hidden)void capture();},seconds*1000);
    status(`定时识别已开启 · 每 ${seconds} 秒`);
    void capture();
  }
  function metric(candidate) {const mean=candidate.mean;const se=candidate.se;return Number.isFinite(mean)?`${mean.toFixed(2)}${Number.isFinite(se)?` ± ${se.toFixed(2)}`:''}`:'暂无估计';}
  function renderRecommendation(result) {
    $('recommendation').classList.remove('stale');
    const primary=result.primary;
    if(!primary){$('recommendation').innerHTML='<div class="recommend-empty"><span>✧</span><h4>当前没有可比较的操作</h4><p>请核对阶段、当前位置与可到达格子。</p></div>';return;}
    const mean=primary.mean,se=primary.se;
    $('recommendation').innerHTML=`<article class="recommend-primary"><div class="recommend-eyebrow"><span>推荐操作</span><span>需在游戏中手动执行</span></div><h4>${escape(primary.label)}</h4><p>${escape(primary.reason||'在当前规则和抽样预算下，这一操作的估计收益较高。')}</p><div class="expected-value"><span>终局绮彩估计</span><strong>${Number.isFinite(mean)?mean.toFixed(2):'—'}</strong><span>个${Number.isFinite(se)?` · 标准误 ${se.toFixed(2)}`:''}</span></div></article>${(result.alternatives||[]).length?'<div class="alternatives-title">其他可选动作</div>':''}${(result.alternatives||[]).slice(0,3).map(candidate=>`<article class="alternative"><div class="alternative-heading"><span>${escape(candidate.label)}</span><span>${escape(metric(candidate))}</span></div><p>${escape(candidate.reason||'')}</p></article>`).join('')}<p class="simulation-stats">${escape(result.stats?.samples??'—')} 次抽样${Number.isFinite(result.stats?.elapsedMs)?` · ${(result.stats.elapsedMs/1000).toFixed(2)} 秒`:''} · ± 为抽样标准误，非成功率</p>${(result.warnings||[]).map(warning=>`<p class="recommend-warning">⚑ ${escape(warning.message||warning)}</p>`).join('')}`;
  }
  async function recommend(allowConfirmation=false,fromScan=false) {
    if(!board){toast('请先导入截图或加载示例局面。');return;}
    if(!serviceReady){toast('本地服务尚未就绪，请先重试连接。');return;}
    if((scanBusy||captureBusy)&&!fromScan){toast('正在处理新画面，请等待识别完成后再比较。');return;}
    if(!board.confirmed){if(!allowConfirmation){markStale();return;}board.confirmed=true;boardRevision++;}
    const sequence=++recommendSequence,revision=boardRevision;
    $('recommend-status').textContent='正在比较…';$('recommend-status').className='status-tag';$('recommend-button').disabled=true;$('recommend-button').textContent='正在模拟后续操作…';
    try {const result=await api.recommend(clone(board),{budgetMs:1800,rollouts:64});if(sequence!==recommendSequence||revision!==boardRevision){markStale();return;}lastResult=result;renderRecommendation(result);$('recommend-status').textContent=sourceType.startsWith('示例')?'示例建议':'比较完成';$('recommend-status').className='status-tag ready';}
    catch(error){if(sequence===recommendSequence&&revision===boardRevision){$('recommend-status').textContent='暂不可用';$('recommend-status').className='status-tag stale';toast(`建议生成失败：${errorMessage(error)}`);}}
    finally{if(sequence===recommendSequence){$('recommend-button').disabled=scanBusy||captureBusy||visionBusy||!serviceReady;$('recommend-button').textContent=board?.confirmed===false?'已核对棋盘，计算建议':'重新比较下一步';}}
  }
  async function demo() {
    if(!serviceReady){toast('本地服务尚未就绪，请先重试连接。');return;}
    if(scanBusy||captureBusy){toast('正在处理截图，请稍候再加载示例。');return;}
    const revision=boardRevision;
    $('demo-button').disabled=true;
    try{const state=await api.demo();if(revision!==boardRevision)return;setBoard(state.board||state,'示例局面');renderIssues([{message:'当前为示例数据，不对应你的游戏画面。导入截图后请重新核对。'}]);status('已加载示例局面 · 非游戏实况');await recommend();}
    catch(error){toast(`示例载入失败：${errorMessage(error)}`);}
    finally{$('demo-button').disabled=false;}
  }
  function updateVisionControls() {
    const busy=scanBusy||captureBusy||visionBusy;
    $('new-board-button').disabled=!serviceReady||busy;
    $('demo-button').disabled=!serviceReady||busy;
    $('calibrate-button').disabled=!currentImageData||busy;
    $('rescan-button').disabled=!serviceReady||!visionReady||!currentImageData||busy;
    $('learn-vision-button').disabled=!serviceReady||busy||!currentImageData||!board||board.source==='demo'||!$('vision-confirm').checked||calibration?.points?.length!==16;
    $('export-vision-button').disabled=!serviceReady||!visionReady||visionBusy;
    $('reset-vision-button').disabled=!serviceReady||!visionReady||busy;
    $('vision-profile-input').disabled=!serviceReady||busy;
    $('service-retry').disabled=initializing;
  }
  function displayVisionProfile(result) {
    visionReady=!!result?.ready;
    const summary=typeof result?.summary==='string'?result.summary:'';
    if(result?.profile?.calibration?.points?.length===16)calibration=clone(result.profile.calibration);
    $('vision-status').textContent=visionReady?'已有本机样本 · 仍需核对':'首次需教会识图';
    $('vision-status').className=visionReady?'status-tag ready':'status-tag stale';
    const counts=result?.counts;
    const coverage=counts?` 已见 ${Object.values(counts.kind||{}).filter(n=>n>0).length} 类房间、${Object.values(counts.beast||{}).filter(n=>n>0).length} 种囚犯、${Object.values(counts.tier||{}).filter(n=>n>0).length} 个阶级。`:'';
    $('vision-summary').textContent=`${summary|| (visionReady?'已加载本机识图样本。':'尚未学习样本。')}${coverage} 首次需教会识图；尚未验证真实准确率。全程在本机运行，无需联网。`;
    updateVisionControls();
  }
  async function newBoard() {
    if(!serviceReady||scanBusy||captureBusy||visionBusy)return;
    const revision=boardRevision;$('new-board-button').disabled=true;
    try {
      const state=await api.newBoard();if(revision!==boardRevision)return;
      const manual={...(state.board||state),source:'manual',confirmed:false};
      setBoard(manual,'手动录入');$('vision-confirm').checked=false;
      renderIssues([{message:'这是待手工录入的空棋盘。请逐格对照当前截图，设置房间、种族与阶级，再核对天数、位置、刑具和事件进度。'}]);
      status(currentImageData?'截图已保留 · 请手工录入并核对本张截图的棋盘。':'空棋盘已创建 · 请先导入截图，或手工录入局面。');
    }catch(error){toast(`创建棋盘失败：${errorMessage(error)}`);}
    finally{updateVisionControls();}
  }
  async function loadCurrentImage() {
    if(!currentImageData)throw new Error('请先截图或导入图片。');
    const img=new Image();img.src=currentImageData;await img.decode();return img;
  }
  function drawCalibration() {
    if(!calibrationImage)return;
    const canvas=$('calibration-canvas'),ctx=canvas.getContext('2d');
    canvas.width=calibrationImage.naturalWidth;canvas.height=calibrationImage.naturalHeight;
    ctx.drawImage(calibrationImage,0,0);
    const radius=Math.max(12,Math.min(canvas.width,canvas.height)*.016);
    calibrationDraft.forEach((point,index)=>{
      const x=point.x*canvas.width,y=point.y*canvas.height;
      ctx.beginPath();ctx.arc(x,y,radius,0,Math.PI*2);ctx.fillStyle='#e7b572';ctx.fill();ctx.lineWidth=Math.max(2,radius*.12);ctx.strokeStyle='#171d17';ctx.stroke();
      ctx.fillStyle='#151b16';ctx.font=`700 ${radius}px "Segoe UI",sans-serif`;ctx.textAlign='center';ctx.textBaseline='middle';ctx.fillText(String(index+1).padStart(2,'0'),x,y+1);
    });
    $('calibration-progress').textContent=calibrationDraft.length===16?'16 / 16 已标定 · 请检查顺序，再保存位置':`请点击 ${String(calibrationDraft.length+1).padStart(2,'0')} · ${calibrationDraft.length?'下一格中心（顺时针）':'审讯室中心'}`;
    $('calibration-dimensions').textContent=`${canvas.width} × ${canvas.height} 像素`;
    $('save-calibration').disabled=calibrationDraft.length!==16;
    $('undo-calibration').disabled=!calibrationDraft.length;
  }
  async function openCalibration() {
    const revision=imageRevision;
    try {
      const img=await loadCurrentImage();if(revision!==imageRevision)return;
      calibrationImage=img;calibrationImageRevision=revision;
      calibrationDraft=calibration?.points?.length===16?clone(calibration.points):[];
      drawCalibration();$('calibration-dialog').showModal();
    }catch(error){toast(`无法打开校准画面：${errorMessage(error)}`);}
  }
  async function learnVision() {
    if(!serviceReady||scanBusy||captureBusy||visionBusy)return;
    if(!currentImageData||!board||board.source==='demo'||!$('vision-confirm').checked){toast('请手工录入本张截图的棋盘，并勾选已逐格核对。');return;}
    if(calibration?.points?.length!==16){toast('请先校准并保存 16 个房间中心。');return;}
    const sequence=++visionSequence,revision=imageRevision,dataUrl=currentImageData,learnedBoard=clone(board),positions=clone(calibration);
    visionBusy=true;setBusyButtons();status('正在本机学习已核对样本…');
    try {
      const img=await loadCurrentImage();if(revision!==imageRevision)return;
      positions.width=img.naturalWidth;positions.height=img.naturalHeight;
      const result=await api.learnVision({dataUrl,board:learnedBoard,calibration:positions});
      if(sequence!==visionSequence)return;
      displayVisionProfile(result);$('vision-confirm').checked=false;
      status(result.ready?'样本已在本机保存 · 点击“重新识别当前截图”验证。':'样本已保存，仍需补充不同牢房状态或囚犯类型。');
      toast(result.summary||'离线样本已保存。请重新识别并逐格检查结果。');
    }catch(error){toast(`样本学习失败：${errorMessage(error)}`);status('样本未能保存，请检查校准点与手工录入的棋盘。');}
    finally{visionBusy=false;setBusyButtons();}
  }
  async function exportVision() {
    if(!serviceReady||visionBusy)return;
    try {
      const result=await api.visionProfile();if(!result.profile)throw new Error('还没有可导出的本机样本。');
      const blob=new Blob([JSON.stringify(result.profile,null,2)],{type:'application/json'}),url=URL.createObjectURL(blob),link=document.createElement('a');
      link.href=url;link.download='torchlight-vision-profile.json';document.body.appendChild(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),1000);
      toast('已导出位置与数值特征，不包含原始截图。');
    }catch(error){toast(`导出失败：${errorMessage(error)}`);}
  }
  async function importVision(file) {
    if(!file||!serviceReady||scanBusy||captureBusy||visionBusy)return;
    if(file.size>2*1024*1024){toast('校准 JSON 过大，请使用小于 2 MB 的文件。');return;}
    const sequence=++visionSequence;visionBusy=true;setBusyButtons();
    try {
      const profile=JSON.parse(await file.text()),result=await api.importVision(profile);
      if(sequence!==visionSequence)return;
      displayVisionProfile(result);$('vision-confirm').checked=false;
      status('离线识图样本已导入 · 请检查分辨率与房间位置，再用截图验证。');
      toast(result.summary||'已导入朋友的离线识图样本。请先核对校准位置。');
    }catch(error){toast(`导入失败：${errorMessage(error)}`);}
    finally{visionBusy=false;setBusyButtons();}
  }
  async function resetVision() {
    if(!serviceReady||scanBusy||captureBusy||visionBusy)return;
    const sequence=++visionSequence;visionBusy=true;setBusyButtons();
    try {const result=await api.resetVision();if(sequence!==visionSequence)return;visionReady=false;calibration=null;calibrationDraft=[];$('vision-confirm').checked=false;$('auto-input').checked=false;clearInterval(timer);timer=null;displayVisionProfile(result);boardRevision++;markStale();status('本机识图样本已清除 · 请重新校准并学习。');}
    catch(error){toast(`清除样本失败：${errorMessage(error)}`);}
    finally{visionBusy=false;setBusyButtons();}
  }
  function syncCellFields() {
    const inmate=$('cell-kind').value==='inmate';
    $('cell-beast').disabled=!inmate;$('cell-tier').disabled=!inmate;
    $('cell-due').disabled=$('cell-kind').value!=='nest';
    $('cell-event-kind').disabled=!$('cell-event').checked;
    const lion=inmate&&$('cell-beast').value==='lion';
    $('cell-prey').disabled=!lion;$('cell-prey-rounds').disabled=!lion;
  }
  function openCell(n) {
    if(!board)return;
    editCell=n;const cell=board.cells[n];
    $('cell-title').textContent=`${n+1} 号 · 编辑牢房`;
    $('cell-kind').value=cell.kind;$('cell-kind').disabled=n===0;
    $('cell-kind').querySelector('option[value="office"]').disabled=n!==0;
    $('cell-beast').value=cell.beast||'mammoth';$('cell-tier').value=cell.tier||0;
    $('cell-event').checked=!!cell.event;$('cell-event-kind').value=cell.eventKind||'';
    $('cell-due').min=board.day+1;$('cell-due').value=cell.due||board.day+2;
    $('cell-prey').value=cell.prey?cell.prey.cell+1:'';$('cell-prey-rounds').value=cell.prey?.rounds||1;
    $('cell-ivory').checked=!!cell.ivory;$('cell-scratch').checked=!!cell.scratch;
    $('cell-position').checked=n===board.position;
    syncCellFields();$('cell-dialog').showModal();
  }
  for(const id of ['cell-kind','cell-beast','cell-event'])$(id).addEventListener('change',syncCellFields);
  $('cell-form').addEventListener('submit',event=>{
    event.preventDefault();if(!board)return;
    const kind=$('cell-kind').value;
    const cell={...board.cells[editCell],n:editCell,kind,beast:$('cell-beast').value,tier:Number($('cell-tier').value),event:$('cell-event').checked,ivory:$('cell-ivory').checked,scratch:$('cell-scratch').checked};
    if(cell.event&&$('cell-event-kind').value)cell.eventKind=$('cell-event-kind').value;else delete cell.eventKind;
    if(kind==='nest')cell.due=Number($('cell-due').value);else delete cell.due;
    if(kind==='inmate'&&cell.beast==='lion'&&$('cell-prey').value)cell.prey={cell:Number($('cell-prey').value)-1,rounds:Number($('cell-prey-rounds').value)||1};else delete cell.prey;
    board.cells[editCell]=cell;
    if($('cell-position').checked)board.position=editCell;
    changed();$('cell-dialog').close();void recommend();
  });
  $('close-cell').addEventListener('click',()=>$('cell-dialog').close());
  $('close-report').addEventListener('click',()=>$('report-dialog').close());
  $('edit-state-button').addEventListener('click',()=>{const expanded=document.querySelector('.advanced-settings').classList.toggle('expanded');$('edit-state-button').textContent=expanded?'收起更多信息':'编辑更多信息';document.querySelector('.advanced-settings').scrollIntoView({behavior:'smooth',block:'nearest'});});
  $('capture-button').addEventListener('click',()=>void capture());$('refresh-sources').addEventListener('click',()=>void refreshSources());$('demo-button').addEventListener('click',()=>void demo());$('recommend-button').addEventListener('click',()=>void recommend(true));
  $('reset-button').addEventListener('click',()=>resetContext());
  $('new-board-button').addEventListener('click',()=>void newBoard());
  $('calibrate-button').addEventListener('click',()=>void openCalibration());
  $('close-calibration').addEventListener('click',()=>$('calibration-dialog').close());
  $('calibration-canvas').addEventListener('click',event=>{
    if(!calibrationImage||calibrationDraft.length>=16)return;
    const rect=event.currentTarget.getBoundingClientRect(),x=(event.clientX-rect.left)/rect.width,y=(event.clientY-rect.top)/rect.height;
    if(x<0||x>1||y<0||y>1)return;
    calibrationDraft.push({x,y});drawCalibration();
  });
  $('undo-calibration').addEventListener('click',()=>{calibrationDraft.pop();drawCalibration();});
  $('clear-calibration').addEventListener('click',()=>{calibrationDraft=[];drawCalibration();});
  $('save-calibration').addEventListener('click',()=>{
    if(calibrationDraft.length!==16||!calibrationImage)return;
    if(calibrationImageRevision!==imageRevision){toast('截图已变化，请重新打开本张截图进行校准。');$('calibration-dialog').close();return;}
    calibration={width:calibrationImage.naturalWidth,height:calibrationImage.naturalHeight,points:clone(calibrationDraft)};
    $('calibration-dialog').close();$('vision-confirm').checked=false;updateVisionControls();
    status('16 个位置已暂存 · 请创建 / 核对棋盘，再学习样本。');
  });
  $('vision-confirm').addEventListener('change',updateVisionControls);
  $('learn-vision-button').addEventListener('click',()=>void learnVision());
  $('rescan-button').addEventListener('click',()=>{if(currentImageData)void analyzeImage(currentImageData,'当前截图');});
  $('export-vision-button').addEventListener('click',()=>void exportVision());
  $('vision-profile-input').addEventListener('change',event=>{const file=event.target.files[0];if(file)void importVision(file);event.target.value='';});
  $('vision-profile-input').closest('label').addEventListener('keydown',event=>{if(event.key==='Enter'||event.key===' '){event.preventDefault();$('vision-profile-input').click();}});
  $('reset-vision-button').addEventListener('click',()=>void resetVision());
  $('service-retry').addEventListener('click',()=>void initialize());
  $('image-input').addEventListener('change',event=>{const file=event.target.files[0];if(file)void importImage(file);event.target.value='';});
  document.querySelector('.file-button').addEventListener('keydown',event=>{if(event.key==='Enter'||event.key===' '){event.preventDefault();$('image-input').click();}});
  document.addEventListener('paste',event=>{const image=Array.from(event.clipboardData?.items||[]).find(item=>item.type.startsWith('image/'));if(image){event.preventDefault();void importImage(image.getAsFile());}});
  $('screenshot-area').addEventListener('dragover',event=>event.preventDefault());$('screenshot-area').addEventListener('drop',event=>{event.preventDefault();if(event.dataTransfer.files[0])void importImage(event.dataTransfer.files[0]);});
  $('auto-input').addEventListener('change',configureAuto);$('interval-input').addEventListener('change',()=>{if($('auto-input').checked)configureAuto();});$('source-input').addEventListener('change',()=>resetContext('截图来源已切换 · 请获取新截图；需要时重新开启定时识别。'));
  $('day-input').addEventListener('change',()=>{if(!board)return;board.day=Math.max(1,Math.min(20,Number($('day-input').value)||1));$('day-input').value=board.day;changed();});
  $('day-minus').addEventListener('click',()=>{if(!board)return;$('day-input').value=Math.max(1,board.day-1);$('day-input').dispatchEvent(new Event('change'));});$('day-plus').addEventListener('click',()=>{if(!board)return;$('day-input').value=Math.min(20,board.day+1);$('day-input').dispatchEvent(new Event('change'));});
  $('phase-input').addEventListener('change',()=>{if(board){board.phase=$('phase-input').value;changed();}});
  $('destinations-input').addEventListener('change',()=>{if(!board)return;const input=$('destinations-input').value.trim();const values=input?input.split(/[,，、\s]+/).map(Number):[];if(values.some(n=>!Number.isInteger(n)||n<1||n>16)){toast('可到达格子请输入 1–16 的编号，用逗号分隔。');syncControls();return;}board.destinations=[...new Set(values)].map(n=>n-1);changed();});
  $('tools-input').addEventListener('change',()=>{if(board){board.tools=Array.from($('tools-input').selectedOptions).map(option=>option.value);changed();}});
  for(const [id,key] of [['objective-input','objective'],['progress-input','objectiveProgress'],['vault-input','vault'],['milestone-input','milestone'],['used-action-input','usedAction'],['no-swap-input','noSwap'],['no-devour-input','noDevour'],['chain-armed-input','chainArmed'],['event-input','eventKind']]){$(id).addEventListener('change',()=>{if(!board)return;const input=$(id);board[key]=input.type==='checkbox'?input.checked:input.type==='number'?Math.max(0,Number(input.value)||0):input.value||null;changed();});}
  $('offers-input').addEventListener('change',()=>{if(board){board.offerIds=Array.from($('offers-input').selectedOptions).map(option=>option.value);changed();}});
  $('usurp-stage').addEventListener('change',()=>{if(board){const value=$('usurp-stage').value;board.usurpStage=/^[1-4]$/.test(value)?Number(value):value;changed();}});
  $('resume-phase').addEventListener('change',()=>{if(board){board.resumePhase=$('resume-phase').value||null;changed();}});
  $('pin-button').addEventListener('click',async()=>{const target=!alwaysOnTop;try{const result=await api.setAlwaysOnTop(target);alwaysOnTop=typeof result==='boolean'?result:target;$('pin-button').setAttribute('aria-pressed',String(alwaysOnTop));}catch(error){toast(errorMessage(error));}});
  $('compact-button').addEventListener('click',async()=>{const target=!compact;try{const result=await api.setCompact(target);compact=typeof result==='boolean'?result:target;document.body.classList.toggle('compact',compact);$('compact-button').setAttribute('aria-pressed',String(compact));$('compact-button').querySelector('span').textContent=compact?'完整视图':'精简悬浮';}catch(error){toast(errorMessage(error));}});
  $('report-button').addEventListener('click',async()=>{$('report-dialog').showModal();$('report-content').textContent='正在读取本地评估报告…';try{const report=typeof api.report==='function'?await api.report():await request('report');$('report-content').innerHTML='<p>以下为本地训练与评估记录。模拟表现不等于真实游戏表现，需结合规则覆盖和截图识别准确性判断。</p><pre>'+escape(JSON.stringify(report,null,2))+'</pre>';}catch(error){$('report-content').textContent=`尚无可用评估报告：${errorMessage(error)}`;}});
  window.addEventListener('beforeunload',()=>clearInterval(timer));
  if(typeof api.onCapture==='function')api.onCapture(result=>{const dataUrl=typeof result==='string'?result:result?.dataUrl;if(scanBusy||captureBusy)return;if(dataUrl)void analyzeImage(dataUrl,'快捷键截图');else void capture();});
  if(typeof api.onWindowState==='function')api.onWindowState(state=>{if(typeof state?.compact==='boolean'){compact=state.compact;document.body.classList.toggle('compact',compact);$('compact-button').setAttribute('aria-pressed',String(compact));$('compact-button').querySelector('span').textContent=compact?'完整视图':'精简悬浮';}if(typeof state?.alwaysOnTop==='boolean'){alwaysOnTop=state.alwaysOnTop;$('pin-button').setAttribute('aria-pressed',String(alwaysOnTop));}});
  async function initialize() {
    if(initializing)return;
    initializing=true;serviceReady=false;setBusyButtons();
    $('connection-status').textContent='正在连接本地服务…';$('connection-status').classList.remove('error');
    renderBoard();
    try {
      bootstrapData=await api.bootstrap();
      $('tools-input').innerHTML=(bootstrapData.tools||[]).map(tool=>`<option value="${escape(tool.id)}">${escape(tool.name)}</option>`).join('');
      $('event-input').innerHTML='<option value="">无 / 未确认</option>'+(bootstrapData.events||[]).map(event=>`<option value="${escape(event.id)}">${escape(event.name)}</option>`).join('');
      $('cell-event-kind').innerHTML='<option value="">未确认的随机事件</option>'+(bootstrapData.events||[]).map(event=>`<option value="${escape(event.id)}">${escape(event.name)}</option>`).join('');
      $('offers-input').innerHTML=[...(bootstrapData.tools||[]),...(bootstrapData.gifts||[])].map(item=>`<option value="${escape(item.id)}">${escape(item.name)}</option>`).join('');
      const vision=typeof api.visionProfile==='function'?await api.visionProfile():bootstrapData.vision||{ready:false};
      serviceReady=true;displayVisionProfile(vision);syncControls();
      $('connection-status').textContent=window.torchlight?'本机离线助手已就绪':'本机离线浏览器预览';
      $('service-error').hidden=true;$('service-retry').hidden=true;
      const policy=bootstrapData.policy||{};$('policy-label').textContent=policy.name||policy.label||'普通 20 天 · 绮彩优先';
    }
    catch(error){
      serviceReady=false;$('connection-status').textContent='本地服务未连接';$('connection-status').classList.add('error');
      $('service-error').textContent=`本地服务未就绪：${errorMessage(error)}。可以先导入截图保留预览；识别、学习和建议需连接成功后使用。请点击“重试连接”。`;
      $('service-error').hidden=false;$('service-retry').hidden=false;
    }
    finally{initializing=false;setBusyButtons();}
    await refreshSources();
  }
  void initialize();
})();
