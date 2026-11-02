mergeInto(LibraryManager.library, {
  MahjongUnityEmit: function (pointer) {
    var bridge = window.MahjongUnityBridge;
    if (!bridge || typeof bridge.emit !== "function") {
      console.error("Mahjong renderer: MahjongUnityBridge.emit is not installed.");
      return 0;
    }
    try {
      bridge.emit(UTF8ToString(pointer));
      return 1;
    } catch (_) {
      console.error("Mahjong renderer: browser callback failed.");
      return 0;
    }
  }
});
