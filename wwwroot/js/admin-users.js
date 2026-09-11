(() => {
    document.querySelectorAll('form[data-confirm]').forEach(form => form.addEventListener('submit', event => {
        const message = form.dataset.confirm;
        if (message && !window.confirm(message)) event.preventDefault();
    }));
    document.querySelectorAll('[data-confirm]').forEach(button => {
        if (button.tagName === 'FORM') return;
        button.addEventListener('click', event => {
            const message = button.dataset.confirm;
            if (message && !window.confirm(message)) event.preventDefault();
        });
    });
})();
