// Dynamic Island presses Ctrl+Shift+9 (a global shortcut, so it works while the browser is in the
// background). We find the tab that's playing sound and toggle picture-in-picture for its video.
chrome.commands.onCommand.addListener(async (command) => {
  if (command !== 'toggle-pip') return;

  let [tab] = await chrome.tabs.query({ audible: true });
  if (!tab) [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (!tab) return;

  try {
    await chrome.scripting.executeScript({ target: { tabId: tab.id, allFrames: true }, func: togglePip });
  } catch (e) {
    // Pages like the extensions page or the web store can't be scripted
  }
});

function togglePip() {
  if (document.pictureInPictureElement) {
    document.exitPictureInPicture();
    return;
  }

  // The biggest video that's playing (or the biggest one at all)
  const videos = [...document.querySelectorAll('video')]
    .filter((v) => v.readyState > 0)
    .sort((a, b) => b.offsetWidth * b.offsetHeight - a.offsetWidth * a.offsetHeight);
  const video = videos.find((v) => !v.paused) ?? videos[0];
  if (!video) return;

  video.removeAttribute('disablePictureInPicture');
  video.requestPictureInPicture().catch(() => {});
}
