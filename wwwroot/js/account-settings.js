(() => {
    const confirmation = document.querySelector('[data-account-confirm]');
    if (confirmation) {
        confirmation.showModal();
        confirmation.addEventListener('cancel', event => {
            event.preventDefault();
            confirmation.querySelector('[data-account-cancel]').requestSubmit();
        });
    }
    document.querySelectorAll('[data-account-dialog]').forEach(button => {
        button.addEventListener('click', () => document.getElementById(button.dataset.accountDialog)?.showModal());
    });
    document.querySelectorAll('[data-account-close]').forEach(button => {
        button.addEventListener('click', () => button.closest('dialog').close());
    });
    document.querySelectorAll('.account-dialog').forEach(dialog => {
        dialog.addEventListener('close', () => dialog.querySelectorAll('input[type=password]').forEach(input => input.value = ''));
    });
})();
