#!/usr/bin/env python3.12
"""Reproduce the Linux Tkinter + Node integration check without running a PE.

Requires Python 3.12, Tkinter, Pillow, NumPy, Node and a working DISPLAY (Xvfb
is sufficient). The original engine/vision .pyc files must be the audited,
Python 3.12 extraction from the supplied original EXE. All staging is under a
temporary directory; original files are read only. Network calls are blocked.

Example:
    DISPLAY=:0 python3.12 legacy-update/test-integration.py \
        --zip dist/TalosHelper-v0.3.1-Offline-Update.zip \
        --original-dir /workspace/legacy-talos-v03 \
        --extracted-dir /workspace/scratch/talos-v03-extracted \
        --output legacy-update/integration-validation.json

This checks UI/control flow on constructed fields and synthetic frame changes.
It does not establish Windows executable compatibility or game OCR accuracy.
If fontEnvironment.chineseTextWidth is zero, the Xvfb/Tk core-font setup needs
a CJK font before the PNG files can be used for text readability checks.
"""
import argparse,copy,hashlib,importlib.util,json,marshal,os,shutil,socket,subprocess,sys,tempfile,threading,time,traceback,types,zipfile
from pathlib import Path

P=argparse.ArgumentParser(description=__doc__)
P.add_argument('--repo',type=Path,default=Path(__file__).resolve().parent.parent if Path(__file__).resolve().parent.name=='legacy-update' else Path('/workspace/torchlightinfinite'))
P.add_argument('--zip',type=Path,default=None)
P.add_argument('--output',type=Path,default=None)
P.add_argument('--artifacts-dir',type=Path,default=None)
P.add_argument('--original-dir',type=Path,default=Path('/workspace/legacy-talos-v03'))
P.add_argument('--extracted-dir',type=Path,default=Path('/workspace/scratch/talos-v03-extracted'))
args=P.parse_args()
REPO=args.repo.resolve();ORIGINAL=args.original_dir.resolve();EXTRACTED=args.extracted_dir.resolve()
args.zip=args.zip or REPO/'dist/TalosHelper-v0.3.1-Offline-Update.zip'
args.output=args.output or REPO/'legacy-update/integration-validation.json'
args.artifacts_dir=args.artifacts_dir or REPO/'artifacts'

report={'schema':'talos-tk-node-integration-v1','passed':False,'platform':sys.platform,'python':sys.version.split()[0],'windowsExeExecuted':False,'realGameScreenshotAccuracyClaimed':False,'testScope':'Patched Python entry under Tkinter/Xvfb + real local Node adapter. Constructed fields and synthetic frame-change stimuli only; no Windows EXE or real game tested.','checks':{},'errors':[]}
original_hash=hashlib.sha256((ORIGINAL/'TalosHelper.exe').read_bytes()).hexdigest();report['originalExeSha256']=original_hash
network_attempts=[];thread_errors=[];tk_errors=[];cross_thread_writes=[];main_thread=threading.get_ident()
def denied(*a,**kw):
 network_attempts.append('Python socket operation blocked');raise RuntimeError('Network forbidden in integration QA')
socket.socket.connect=denied;socket.socket.connect_ex=denied;socket.socket.sendto=denied;socket.create_connection=denied
threading.excepthook=lambda event:thread_errors.append(''.join(traceback.format_exception(event.exc_type,event.exc_value,event.exc_traceback)))

def load(name,path):
 spec=importlib.util.spec_from_file_location(name,path);module=importlib.util.module_from_spec(spec);sys.modules[name]=module;spec.loader.exec_module(module);return module

def check(name,condition,detail=None):
 if not condition:raise AssertionError(name+': '+str(detail))
 report['checks'][name]={'passed':True,**({'detail':detail} if detail is not None else {})}

with tempfile.TemporaryDirectory(prefix='talos-final-integration-') as td:
 stage=Path(td);root=None;release=threading.Event();release.set();held=threading.Event();delay={'enabled':False};node_runs=[];real_run=subprocess.run
 try:
  builder=load('talos_integration_builder',REPO/'legacy-update/build-update.py')
  check('actual_update_zip_exists',args.zip.is_file(),str(args.zip))
  report['updateZipSha256']=hashlib.sha256(args.zip.read_bytes()).hexdigest()
  report['updateZipBytes']=args.zip.stat().st_size
  with zipfile.ZipFile(args.zip) as z:
   check('update_zip_crc',z.testzip() is None)
   for name in ['talos_game_update.py','training/game-runtime.cjs','training/game-advice.cjs','training/game-metadata.json','training/game-validation.json']:
    destination=stage/name;destination.parent.mkdir(parents=True,exist_ok=True);destination.write_bytes(z.read(name))
   exe=z.read(builder.NEW_EXE_NAME)
  archive=builder.Archive.read(exe);code=marshal.loads(archive.entry('app').content())
  original_archive=builder.Archive.read((ORIGINAL/'TalosHelper.exe').read_bytes())
  check('original_pyz_bytes_preserved',archive.entry('PYZ.pyz').packed==original_archive.entry('PYZ.pyz').packed)
  helper_bytes=(stage/'talos_game_update.py').read_bytes();helper_hash=hashlib.sha256(helper_bytes).hexdigest();report['helperSha256']=helper_hash;report['updatedExeSha256']=hashlib.sha256(exe).hexdigest()
  shutil.copytree(ORIGINAL/'english-source',stage/'english-source')
  (stage/'training/english').mkdir(parents=True,exist_ok=True);shutil.copy2(ORIGINAL/'training/english/policy.json',stage/'training/english/policy.json')
  (stage/'runtime').mkdir();(stage/'runtime/node.exe').symlink_to(shutil.which('node'));report['nodeRuntime']='Linux system node symlink under temporary runtime/node.exe; Windows runtime not executed'
  guard=stage/'deny-network.cjs';guard_log=stage/'node-network-attempts.txt'
  guard.write_text("const fs=require('node:fs');const deny=()=>{fs.appendFileSync(process.env.TALOS_QA_NETWORK_LOG,'blocked\\n');throw new Error('Network forbidden in integration QA')};for(const n of ['net','tls']){const m=require('node:'+n);m.connect=deny;m.createConnection=deny;if(m.Socket)m.Socket.prototype.connect=deny;}for(const n of ['http','https']){const m=require('node:'+n);m.request=deny;m.get=deny;}globalThis.fetch=deny;\n")
  os.environ['NODE_OPTIONS']=(os.environ.get('NODE_OPTIONS','')+' --require='+str(guard)).strip();os.environ['TALOS_QA_NETWORK_LOG']=str(guard_log)
  engine=load('engine',EXTRACTED/'engine.pyc');vision=load('vision',EXTRACTED/'vision.pyc');vision.ROOT=stage
  import tkinter as tk
  from tkinter import messagebox,font
  from PIL import Image,ImageGrab
  modal_errors=[];messagebox.showerror=lambda title,text,**kw:modal_errors.append({'title':title,'text':str(text)});messagebox.showinfo=lambda *a,**kw:None
  old_set=tk.Variable.set
  def observed_set(self,value):
   if threading.get_ident()!=main_thread:cross_thread_writes.append(str(value)[:100])
   return old_set(self,value)
  tk.Variable.set=observed_set
  app_module=types.ModuleType('talos_integration_app');app_module.__file__=str(stage/'app.py');sys.modules[app_module.__name__]=app_module;exec(code,app_module.__dict__)
  root=tk.Tk();root.report_callback_exception=lambda *error:tk_errors.append(''.join(traceback.format_exception(*error)))
  qa_font=font.Font(root,family='Microsoft YaHei UI',size=11)
  report['fontEnvironment']={'tkVersion':str(root.tk.call('info','patchlevel')),'requestedFamily':'Microsoft YaHei UI','actual':qa_font.actual(),'chineseTextWidth':qa_font.measure('当前策略'),'note':'Linux/Xvfb font substitution only; does not establish Windows font metrics.'}
  report['recognitionEvents']=[]
  report['recognitionStimulus']='Controlled recognize failure only; actual app.analyze and original finish_analysis are exercised. No OCR accuracy claim.'
  def fail_recognition(image):
   report['recognitionEvents'].append({'stage':'recognize_raise','thread':threading.get_ident()})
   raise ValueError('QA synthetic frame used only to test invalidation')
  # Install before the verified helper captures the host recognizer.
  app_module.recognize=fail_recognition

  # First initialization deliberately sees a tampered temporary helper. The
  # embedded hash must reject it before any helper bytecode can execute.
  (stage/'talos_game_update.py').write_text("raise AssertionError('TAMPERED_HELPER_EXECUTED')\n")
  app=app_module.App(root)
  check('patched_window_title', '0.3.1' in root.title(), root.title())
  check('helper_tamper_rejected_before_execution',bool(modal_errors) and any('校验失败' in x['text'] for x in modal_errors) and not any('TAMPERED_HELPER_EXECUTED' in x['text'] for x in modal_errors),modal_errors)
  (stage/'talos_game_update.py').write_bytes(helper_bytes);app.calculate()
  helper_name='_talos_game_update_v031_'+helper_hash[:16];helper=sys.modules[helper_name]
  check('helper_hash_verified_by_patched_entry',getattr(helper,'__talos_verified_sha256__',None)==helper_hash)
  state=app._talos_game_update
  def wrapped_run(command,*a,**kw):
   is_advice=isinstance(command,(list,tuple)) and any(str(x).endswith('game-advice.cjs') for x in command)
   completed=real_run(command,*a,**kw)
   if is_advice:
    node_runs.append({'returncode':completed.returncode,'result':json.loads(completed.stdout) if completed.returncode==0 else None,'stderr':completed.stderr[:500]})
    if delay['enabled']:held.set();release.wait(10)
   return completed
  subprocess.run=wrapped_run
  def output():return app.output.get('1.0','end').strip()
  def screenshot(widget,name):
   root.update_idletasks();root.update()
   args.artifacts_dir.mkdir(parents=True,exist_ok=True)
   rect=(widget.winfo_rootx(),widget.winfo_rooty(),widget.winfo_rootx()+widget.winfo_width(),widget.winfo_rooty()+widget.winfo_height())
   target=args.artifacts_dir/name
   ImageGrab.grab(bbox=rect,xdisplay=os.environ.get('DISPLAY')).save(target)
   report.setdefault('artifacts',[]).append({'path':str(target),'width':rect[2]-rect[0],'height':rect[3]-rect[1],'purpose':'Actual Tk application layout check only; not a game screenshot or OCR validation.'})

  def pump_until(predicate,timeout=8):
   # Tkinter's existing recognition worker schedules root.after from another
   # thread. Dispatch through a real mainloop, outside the test callback,
   # rather than nesting root.update() inside a long-running Tcl callback.
   deadline=time.monotonic()+timeout
   completed={'ready':False}
   def poll():
    if predicate():
     completed['ready']=True;root.quit()
    elif time.monotonic()>=deadline:
     root.quit()
    else:
     root.after(10,poll)
   root.after(0,poll)
   root.mainloop()
   if not completed['ready']:
    report['diagnosticThreadStacks']={str(thread_id):''.join(traceback.format_stack(frame)) for thread_id,frame in sys._current_frames().items()}
    raise AssertionError('Timed out waiting for integration condition; status='+app.status.get()+'; output='+output()[:200])
  def dialog():
   app.calculate();root.update();d=state['dialog'];check('review_window_opens',d is not None and d.winfo_exists());return d
  def prepare(d):
   for n,row in enumerate(d.cell_vars):
    row['kind'].set(helper.KINDS['office' if n==0 else 'locked']);row['event'].set('无');row['destination'].set(False);row['ivory'].set(False);row['scratch'].set(False)
   for n,beast,tier in [(2,'lion',3),(4,'wolf',3),(7,'rat',4)]:
    d.cell_vars[n]['kind'].set(helper.KINDS['inmate']);d.cell_vars[n]['beast'].set(helper.BEASTS[beast]);d.cell_vars[n]['tier'].set(helper.TIERS[tier])
   d.position.set(2)
   values={'day':'12','phase':helper.PHASES['manage'],'objective':helper.OBJECTIVES['other'],'objectiveProgress':'0','vault':'0','usurpStage':d.usurp_names['1'],'eventKind':'未确认','chainConsumed':'0','resumePhase':'未确认','startEvent':'普通20天开局','milestone':False,'usedAction':False,'chainArmed':False,'preyPresent':False,'usedExtra':False,'devourAfterSwap':False,'unsupportedToolPresent':False,'unsupportedEventPresent':False}
   for name,value in values.items():d.vars[name].set(value)
   d.tools.selection_clear(0,'end');d.offers.selection_clear(0,'end');d.confirmed.set(False)
  def invoke(d):d.submit_button.invoke();root.update()
  def run_scenarios():
   try:
    d=state['dialog'];prepare(d)
    before=len(node_runs);invoke(d);check('no_unconfirmed_calculation',len(node_runs)==before and not state['busy'] and '勾选' in d.error.get(),d.error.get())
    d.confirmed.set(True);d.vars['day'].set('');d.confirmed.set(True);invoke(d);check('missing_day_rejected','当前第几天' in d.error.get() and len(node_runs)==before,d.error.get())
    d.vars['day'].set('12');d.cell_vars[3]['kind'].set(helper.KINDS['unknown']);d.confirmed.set(True);invoke(d);check('unknown_room_rejected','逐格确认' in d.error.get() and len(node_runs)==before,d.error.get())
    d.cell_vars[3]['kind'].set(helper.KINDS['locked']);d.vars['usurpStage'].set('未确认');d.confirmed.set(True);invoke(d);check('unknown_usurp_rejected','僭越进度' in d.error.get() and len(node_runs)==before,d.error.get())
    d.vars['usurpStage'].set(d.usurp_names['1'])
    for flag in ('unsupportedToolPresent','unsupportedEventPresent'):
     d.vars[flag].set(True);d.confirmed.set(True);invoke(d);check(flag+'_rejected',bool(d.error.get()) and len(node_runs)==before,d.error.get());d.vars[flag].set(False)
    d.confirmed.set(True);d.vars['day'].set('13');check('control_edit_clears_confirmation',not d.confirmed.get());d.vars['day'].set('12')
    pin=next(w for w in d.winfo_children()[-1].winfo_children() if isinstance(w,tk.ttk.Checkbutton) and w.cget('text')=='核对窗口置顶');pin.invoke();root.update();check('review_pin_toggle',d.pin.get())
    d.confirmed.set(True);d.error.set('');screenshot(d,'talos-v031-review.png');invoke(d);pump_until(lambda:not state['busy'] and output().startswith('已核对局面'))
    check('real_node_result_rendered',len(node_runs)==before+1 and node_runs[-1]['returncode']==0 and '建议：' in output(),{'primary':node_runs[-1]['result'].get('primary'),'uiOutput':output()[:450]})
    check('manual_without_image_is_labeled','无图手工 / 示例研究（非游戏实况）' in output(),output()[:160])
    check('overlay_created',state.get('overlay') is not None and state['overlay'].winfo_exists());report['overlayTopmostValueUnderXvfb']=bool(state['overlay'].attributes('-topmost'))
    check('overlay_uses_real_action',node_runs[-1]['result']['primary']['label'] in state['overlay_text'].get())
    screenshot(state['overlay'],'talos-v031-overlay.png')
    check('validated_baseline_selection_is_visible',node_runs[-1]['result'].get('validation',{}).get('selected')=='baseline' and '修正规则基线（历史参数复验未胜出）' in output() and '修正规则基线（历史参数复验未胜出）' in state['overlay_text'].get(),node_runs[-1]['result'].get('validation'))
    app.analyze(Image.new('RGB',(640,360),'black'));pump_until(lambda:not app.busy and '旧建议已清除' in output())
    check('new_frame_and_failed_recognition_expire_advice','已核对局面' not in output() and '识别失败' in app.status.get(),{'status':app.status.get(),'output':output()[:160]})
    d=dialog();prepare(d);delay['enabled']=True;held.clear();release.clear();d.confirmed.set(True);invoke(d);pump_until(held.is_set)
    app.analyze(Image.new('RGB',(640,360),'#554433'));pump_until(lambda:not app.busy and '旧建议已清除' in output());release.set();pump_until(lambda:not state['busy'])
    check('late_node_reply_cannot_overwrite_new_frame','已核对局面' not in output() and '旧建议已清除' in output(),output()[:200])
    d=dialog();prepare(d);held.clear();release.clear();d.confirmed.set(True);invoke(d);pump_until(held.is_set)
    app.close();release.set();time.sleep(.2)
    check('worker_threads_finish_after_close',not [t.name for t in threading.enumerate() if t is not threading.main_thread()],[t.name for t in threading.enumerate()])
    check('close_with_pending_delivery',not any('main thread is not in main loop' in err or 'TclError' in err for err in thread_errors),thread_errors)
    check('no_worker_tk_variable_writes',not cross_thread_writes,cross_thread_writes)
    check('no_thread_or_tk_callback_errors',not thread_errors and not tk_errors,{'threads':thread_errors,'tk':tk_errors})
    check('no_network_requests',not network_attempts and not guard_log.exists(),network_attempts)
    check('original_exe_unchanged',hashlib.sha256((ORIGINAL/'TalosHelper.exe').read_bytes()).hexdigest()==original_hash)
    report['nodeCalls']=len(node_runs);report['passed']=True
   except BaseException:
    report['errors'].append(traceback.format_exc());release.set()
    try:app.close()
    except Exception:pass
  run_scenarios()
 except BaseException:
  report['errors'].append(traceback.format_exc());release.set()
 finally:
  subprocess.run=real_run
  if root is not None:
   try:root.destroy()
   except Exception:pass
  report['threadErrors']=thread_errors;report['tkErrors']=tk_errors
  report['networkAttempts']=network_attempts
  args.output.parent.mkdir(parents=True,exist_ok=True);args.output.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n')
print(json.dumps({'passed':report['passed'],'checks':len(report['checks']),'output':str(args.output),'errors':report['errors']},ensure_ascii=False))
raise SystemExit(0 if report['passed'] else 1)
