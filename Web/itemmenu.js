/*
 * Comskip Commercial Segments — item-details "Scan for commercials" menu entry.
 *
 * Injected into jellyfin-web's index.html (see Web/install-overlay.sh, same mechanism
 * as the playback overlay). jellyfin-web has no plugin extension point for the item
 * details page's "..." action sheet, so this watches the DOM for that menu opening and
 * appends a matching button to it — same markup/classes jellyfin-web's own menu items
 * use, so it looks native.
 *
 * VERSION-SENSITIVE FILE — built against this server's Jellyfin 12.0.0 web client.
 * Confirmed live via DOM inspection on 2026-09-11:
 *   - The action sheet container is `.actionSheet.opened`, holding a
 *     `.actionSheetScroller` that directly contains one `button.actionSheetMenuItem`
 *     (also `.listItem.listItem-button.emby-button`, with a `data-id` attribute) per
 *     entry — e.g. `data-id="addtocollection"`, `data-id="refresh"`. Each button holds a
 *     `span.actionsheetMenuItemIcon.listItemIcon.listItemIcon-transparent.material-icons
 *     .<icon-name>` and a `div.listItemBody.actionsheetListItemBody >
 *     div.listItemBodyText.actionSheetItemText` with the label text.
 *   - `window.Dashboard.alert(message)` exists globally (not just on the plugin config
 *     page) and shows a dismissible in-page modal — not a native browser dialog, so it's
 *     safe to use for feedback here.
 *   - `window.ApiClient.getCurrentUser()` resolves a user with `.Policy.IsAdministrator`.
 * If this breaks after a jellyfin-web update, re-check these against the running client
 * (open the item details page, click "...", inspect the action sheet in devtools).
 *
 * Scoped to the item DETAILS page specifically (URL `#/details?id=...`), not per-card
 * overflow menus in list/grid views — the details page is what the plugin's config page
 * already points people to for finding an item's ID, and it's the only place this script
 * can reliably learn which item the open menu belongs to (nothing in the action sheet's
 * own DOM identifies it).
 *
 * Always force-runs (ignores show/channel filters and any existing result) — same
 * one-click-does-something expectation as jellyfin-web's own "Refresh metadata" entry
 * right next to it, and the only way this button is ever useful for something already
 * marked complete. No confirmation prompt, for the same reason "Refresh metadata"
 * doesn't have one — if you don't want that, use the config page's Detect button without
 * Force checked instead.
 */
(function () {
  'use strict';

  var MENU_ITEM_DATA_ID = 'comskip-scan';
  var ICON_CLASS =
    'actionsheetMenuItemIcon listItemIcon listItemIcon-transparent material-icons content_cut';

  var isAdminPromise = null;

  function log(msg) {
    if (window.console && window.console.debug) {
      window.console.debug('[ComskipMenu] ' + msg);
    }
  }

  function notify(message) {
    if (window.Dashboard && typeof window.Dashboard.alert === 'function') {
      window.Dashboard.alert(message);
    } else {
      log(message);
    }
  }

  function checkIsAdmin() {
    if (!isAdminPromise) {
      var apiClient = window.ApiClient;
      isAdminPromise = apiClient && apiClient.getCurrentUser
        ? apiClient.getCurrentUser()
          .then(function (u) { return !!(u && u.Policy && u.Policy.IsAdministrator); })
          .catch(function () { return false; })
        : Promise.resolve(false);
    }
    return isAdminPromise;
  }

  function getCurrentItemId() {
    var hash = window.location.hash || '';
    if (hash.indexOf('#/details') !== 0) {
      return null;
    }
    var qIndex = hash.indexOf('?');
    if (qIndex === -1) {
      return null;
    }
    return new URLSearchParams(hash.slice(qIndex + 1)).get('id');
  }

  function runScan(itemId, button) {
    var apiClient = window.ApiClient;
    button.disabled = true;
    apiClient.ajax({
      type: 'POST',
      url: apiClient.getUrl('ComskipSegments/Detect/' + itemId, { force: true }),
      dataType: 'json'
    }).then(function (r) {
      notify((r && r.Message) || 'Queued.');
    }).catch(function () {
      notify('Comskip scan request failed — check the item still exists and you have permission.');
    }).then(function () {
      button.disabled = false;
    });
  }

  function buildMenuItem(itemId) {
    var button = document.createElement('button');
    button.setAttribute('is', 'emby-button');
    button.type = 'button';
    button.className = 'listItem listItem-button actionSheetMenuItem emby-button';
    button.setAttribute('data-id', MENU_ITEM_DATA_ID);

    var icon = document.createElement('span');
    icon.className = ICON_CLASS;
    icon.setAttribute('aria-hidden', 'true');

    var bodyText = document.createElement('div');
    bodyText.className = 'listItemBodyText actionSheetItemText';
    bodyText.textContent = 'Scan for commercials';

    var body = document.createElement('div');
    body.className = 'listItemBody actionsheetListItemBody';
    body.appendChild(bodyText);

    button.appendChild(icon);
    button.appendChild(body);

    button.addEventListener('click', function (e) {
      e.stopPropagation();
      runScan(itemId, button);
    });

    return button;
  }

  function tryInject(scroller) {
    var itemId = getCurrentItemId();
    if (!itemId || scroller.querySelector('[data-id="' + MENU_ITEM_DATA_ID + '"]')) {
      return;
    }

    checkIsAdmin().then(function (isAdmin) {
      if (!isAdmin || scroller.querySelector('[data-id="' + MENU_ITEM_DATA_ID + '"]')) {
        return; // not an admin, or another mutation callback already added it
      }

      scroller.appendChild(buildMenuItem(itemId));
    });
  }

  var observer = new MutationObserver(function (mutations) {
    for (var i = 0; i < mutations.length; i++) {
      var added = mutations[i].addedNodes;
      for (var j = 0; j < added.length; j++) {
        var node = added[j];
        if (!(node instanceof HTMLElement)) {
          continue;
        }

        var scroller = node.classList && node.classList.contains('actionSheetScroller')
          ? node
          : (node.querySelector && node.querySelector('.actionSheetScroller'));
        if (scroller) {
          tryInject(scroller);
        }
      }
    }
  });

  observer.observe(document.body, { childList: true, subtree: true });

  log('item-menu script loaded');
})();
