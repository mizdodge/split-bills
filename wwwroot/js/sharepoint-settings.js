(() => {
    const form = document.getElementById('sharepoint-settings-form');
    if (!form) return;

    const testButton = document.getElementById('sharepoint-test-button');
    const saveButton = document.getElementById('sharepoint-save-button');
    const listSelect = document.getElementById('sharepoint-list-id');
    const status = document.getElementById('sharepoint-test-status');
    const listStatus = document.getElementById('sharepoint-list-status');
    const siteStatus = document.getElementById('sharepoint-site-status');
    const token = document.getElementById('sharepoint-tested-token');
    const siteId = document.getElementById('sharepoint-site-id');
    const listName = document.getElementById('sharepoint-list-name');
    const listUrl = document.getElementById('sharepoint-list-url');
    const inputs = [...form.querySelectorAll('.sharepoint-input')];

    const text = {
        testing: form.dataset.testingText || 'Testing connection…',
        ready: form.dataset.connectionReadyText || 'Connection successful. Choose a list.',
        failed: form.dataset.connectionFailedText || 'Connection failed.',
        choose: form.dataset.chooseListText || 'Choose a SharePoint list.',
        loaded: form.dataset.listsLoadedText || 'lists available.',
        notConnected: form.dataset.notConnectedText || 'Not connected',
        notLoaded: form.dataset.listNotLoadedText || 'Lists have not been loaded.'
    };

    const invalidate = () => {
        token.value = '';
        siteId.value = '';
        listName.value = '';
        listUrl.value = '';
        saveButton.disabled = true;
        listSelect.disabled = true;
        listSelect.innerHTML = `<option value="">${text.choose}</option>`;
        siteStatus.dataset.hasSite = 'false';
        siteStatus.textContent = '';
        const notConnectedLabel = document.createElement('span');
        notConnectedLabel.textContent = text.notConnected;
        siteStatus.append(notConnectedLabel);
        listStatus.textContent = text.notLoaded;
    };

    inputs.forEach(input => input.addEventListener('input', invalidate));
    listSelect.addEventListener('change', () => {
        const selected = listSelect.options[listSelect.selectedIndex];
        listName.value = selected?.textContent?.trim() || '';
        listUrl.value = selected?.dataset.webUrl || '';
        saveButton.disabled = !token.value || !listSelect.value;
    });

    testButton.addEventListener('click', async () => {
        const data = new FormData();
        data.append('TenantId', form.elements.TenantId.value);
        data.append('ClientId', form.elements.ClientId.value);
        data.append('ClientSecret', form.elements.ClientSecret.value);
        data.append('SiteUrl', form.elements.SiteUrl.value);
        const csrf = form.querySelector('input[name="__RequestVerificationToken"]');
        testButton.disabled = true;
        status.dataset.state = 'loading';
        status.textContent = text.testing;
        invalidate();
        try {
            const response = await fetch(form.dataset.testUrl, {
                method: 'POST',
                body: data,
                headers: { 'X-Requested-With': 'XMLHttpRequest', 'RequestVerificationToken': csrf?.value || '' }
            });
            const payload = await response.json();
            if (!response.ok || !payload.success) throw new Error(payload.message || text.failed);

            token.value = payload.testedStateToken || '';
            siteId.value = payload.siteId || '';
            siteStatus.textContent = '';
            const name = document.createElement('strong');
            name.textContent = payload.siteDisplayName || '';
            const id = document.createElement('small');
            id.textContent = payload.siteId || '';
            siteStatus.append(name, id);
            siteStatus.dataset.hasSite = 'true';
            listSelect.innerHTML = '';
            (payload.lists || []).forEach(item => {
                const option = document.createElement('option');
                option.value = item.id;
                option.textContent = item.displayName;
                if (item.webUrl) option.dataset.webUrl = item.webUrl;
                listSelect.append(option);
            });
            listSelect.disabled = false;
            listSelect.selectedIndex = 0;
            listSelect.dispatchEvent(new Event('change'));
            status.dataset.state = 'success';
            status.textContent = text.ready;
            listStatus.textContent = `${payload.lists.length} ${text.loaded}`;
        } catch (error) {
            status.dataset.state = 'error';
            status.textContent = error.message || text.failed;
            invalidate();
        } finally {
            testButton.disabled = false;
        }
    });
})();
