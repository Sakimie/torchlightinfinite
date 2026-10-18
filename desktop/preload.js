'use strict';

const { contextBridge, ipcRenderer } = require('electron');

const invoke = (method, ...args) => ipcRenderer.invoke(`torchlight:${method}`, ...args);
const subscribe = (channel, callback) => {
  if (typeof callback !== 'function') throw new TypeError('请提供事件处理函数。');
  const listener = (_event, value) => callback(value);
  ipcRenderer.on(channel, listener);
  return () => ipcRenderer.removeListener(channel, listener);
};

contextBridge.exposeInMainWorld('torchlight', Object.freeze({
  bootstrap: () => invoke('bootstrap'),
  sources: () => invoke('sources'),
  capture: (sourceId) => invoke('capture', sourceId),
  scan: (dataUrl, previousBoard) => invoke('scan', dataUrl, previousBoard),
  recommend: (board, options) => invoke('recommend', board, options),
  demo: () => invoke('demo'),
  report: () => invoke('report'),
  newBoard: () => invoke('new-board'),
  visionProfile: () => invoke('vision-profile'),
  learnVision: (payload) => invoke('learn-vision', payload),
  importVision: (profile) => invoke('import-vision', profile),
  resetVision: () => invoke('reset-vision'),
  setAlwaysOnTop: (enabled) => invoke('always-on-top', enabled),
  setCompact: (enabled) => invoke('compact', enabled),
  onCapture: (callback) => subscribe('torchlight:capture-request', callback),
  onWindowState: (callback) => subscribe('torchlight:window-state', callback),
}));
