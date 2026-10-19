// Простое хранилище корзины в браузере(главная страница)
window.cart = JSON.parse(localStorage.getItem('cart') || '[]')
    .filter(item => Number.isInteger(Number(item.variantId)) && Number(item.qty) > 0);
localStorage.setItem('cart', JSON.stringify(window.cart));

// Обновляем счётчик в шапке
function updateCartBadge() {
    const total = window.cart.reduce((sum, item) => sum + item.qty, 0);
    document.querySelector('.badge').textContent = total;
}

document.querySelectorAll('.variant-select').forEach(select => {
    select.addEventListener('change', () => {
        const button = select.closest('.product-card').querySelector('.add-to-cart');
        button.disabled = !select.value;
    });
});

document.querySelectorAll('.add-to-cart').forEach(btn => {
    btn.addEventListener('click', () => {
        const select = btn.closest('.product-card').querySelector('.variant-select');
        const option = select.selectedOptions[0];
        const variantId = Number(select.value);
        const stock = Number(option.dataset.stock);
        if (!variantId || stock < 1) return;

        const name = btn.dataset.name;
        const price = parseFloat(btn.dataset.price);
        const existing = window.cart.find(item => Number(item.variantId) === variantId);

        if (existing) {
            if (existing.qty >= stock) {
                alert('В корзине уже максимальное доступное количество этого варианта.');
                return;
            }
            existing.qty++;
            existing.stock = stock;
        } else {
            window.cart.push({
                variantId,
                name,
                size: option.textContent.split(' — ')[0].split(' · ')[0].trim(),
                color: option.textContent.split(' — ')[0].split(' · ').slice(1).join(' · ').trim(),
                price,
                qty: 1,
                stock
            });
        }

        localStorage.setItem('cart', JSON.stringify(window.cart));
        updateCartBadge();
        btn.textContent = '✓ Добавлено';
        setTimeout(() => btn.textContent = 'В корзину', 800);
    });
});

updateCartBadge();
//(конец по главной странице)










if (document.querySelector('.badge')) {
    updateCartBadge();
}