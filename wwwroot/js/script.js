// Простое хранилище корзины в браузере(главная страница)
window.cart = JSON.parse(localStorage.getItem('cart') || '[]');

// Обновляем счётчик в шапке
function updateCartBadge() {
    const total = cart.reduce((sum, item) => sum + item.qty, 0);
    document.querySelector('.badge').textContent = total;
}

// Обработчик всех кнопок «В корзину»
document.querySelectorAll('.add-to-cart').forEach(btn => {
    btn.addEventListener('click', () => {
        const id = btn.dataset.id;
        const name = btn.dataset.name;
        const price = parseFloat(btn.dataset.price);

        const existing = cart.find(i => i.id === id);
        if (existing) {
            existing.qty++;
        } else {
            cart.push({ id, name, price, qty: 1 });
        }

        localStorage.setItem('cart', JSON.stringify(cart));
        updateCartBadge();
        btn.textContent = '✓ Добавлено';
        setTimeout(() => btn.textContent = 'В корзину', 800);
    });
});

updateCartBadge();
//(конец по главной странице)








const addressForm = document.getElementById('addressForm');
if (addressForm) {
    addressForm.addEventListener('submit', e => {
        e.preventDefault();
        alert('Адрес добавлен (пока без бэкенда)');
    });
}

if (document.querySelector('.badge')) {
    updateCartBadge();
}