(function () {
    const token = document.querySelector('meta[name="request-token"]')?.content;
    const panel = document.getElementById('panel');
    const backdrop = document.getElementById('panel-backdrop');
    let panelChanged = false;

    function toast(message) {
        const element = document.createElement('div');
        element.className = 'toast toast-error';
        element.setAttribute('role', 'alert');
        element.textContent = message;
        document.querySelector('.content')?.prepend(element);
    }

    // Slide-in ticket panel
    function openPanel() {
        panel.hidden = false;
        backdrop.hidden = false;
    }

    function closePanel() {
        panel.hidden = true;
        backdrop.hidden = true;
        panel.replaceChildren();
        if (panelChanged) {
            location.reload();
        }
    }

    document.body.addEventListener('htmx:afterSwap', event => {
        if (event.detail.target === panel) {
            openPanel();
        }
    });
    document.body.addEventListener('htmx:beforeRequest', event => {
        if (event.detail.target === panel && event.detail.requestConfig.verb === 'post') {
            panelChanged = true;
        }
    });
    document.body.addEventListener('htmx:responseError', () => toast('That couldn\'t be completed. Please try again.'));
    document.body.addEventListener('htmx:sendError', () => toast('Simple Tasks can\'t be reached right now.'));

    backdrop.addEventListener('click', closePanel);
    panel.addEventListener('click', event => {
        if (event.target.closest('.panel-close')) {
            closePanel();
        }
    });
    document.addEventListener('keydown', event => {
        if (event.key === 'Escape' && !panel.hidden) {
            closePanel();
        }
    });

    // Drag and drop on the board
    const board = document.querySelector('.board');
    if (board?.dataset.canEdit === 'true') {
        board.querySelectorAll('.column-cards').forEach(column => new Sortable(column, {
            group: 'board',
            animation: 120,
            ghostClass: 'card-ghost',
            onEnd: async event => {
                const body = new URLSearchParams({
                    ticketKey: event.item.dataset.key,
                    status: event.to.dataset.status
                });
                if (event.item.previousElementSibling) {
                    body.set('afterKey', event.item.previousElementSibling.dataset.key);
                }
                if (event.item.nextElementSibling) {
                    body.set('beforeKey', event.item.nextElementSibling.dataset.key);
                }

                try {
                    const response = await fetch(board.dataset.moveUrl, {
                        method: 'POST',
                        headers: { 'RequestVerificationToken': token },
                        body
                    });
                    if (!response.ok) {
                        const problem = await response.json().catch(() => null);
                        toast(problem?.error ?? 'That move couldn\'t be saved.');
                        setTimeout(() => location.reload(), 1500);
                        return;
                    }
                    board.querySelectorAll('.column').forEach(section => {
                        section.querySelector('h2 span').textContent = section.querySelectorAll('.card').length;
                    });
                } catch {
                    toast('Simple Tasks can\'t be reached right now.');
                }
            }
        }));
    }

    // Live bell
    if (document.getElementById('bell')) {
        const connection = new signalR.HubConnectionBuilder()
            .withUrl('/hubs/notifications')
            .withAutomaticReconnect()
            .configureLogging(signalR.LogLevel.Error)
            .build();
        connection.on('notify', () => document.body.dispatchEvent(new Event('notify')));
        const start = () => connection.start().catch(() => setTimeout(start, 15000));
        start();
    }
})();
