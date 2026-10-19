// Берём корзину из localStorage (её туда пишет script.js с главной)
cart = JSON.parse(localStorage.getItem('cart') || '[]');

const tbody = document.getElementById('cartTableBody');
const emptyBlock = document.getElementById('emptyCart');
const contentBlock = document.getElementById('cartContent');

// Сохранение и обновление счётчика в шапке
function saveCart() {
    localStorage.setItem('cart', JSON.stringify(cart));
    updateCartBadge(); // функция из script.js
}

// Форматирование цены
function formatPrice(value) {
    return value.toLocaleString('ru-RU') + ' ₽';
}

// Рендер всей корзины
function renderCart() {
    if (cart.length === 0) {
        emptyBlock.classList.remove('d-none');
        contentBlock.classList.add('d-none');
        return;
    }
    emptyBlock.classList.add('d-none');
    contentBlock.classList.remove('d-none');

    tbody.innerHTML = '';

    cart.forEach((item, index) => {
        const row = document.createElement('tr');

        row.innerHTML = `
            <td>
                <div class="d-flex align-items-center gap-3">
                    <img src="https://placehold.co/60x60" alt="" width="60" height="60" class="rounded">
                    <span class="fw-semibold">${item.name}</span>
                </div>
            </td>
            <td class="text-center">
                <div class="input-group input-group-sm justify-content-center" style="max-width:120px; margin:auto;">
                    <button class="btn btn-outline-secondary btn-minus" data-index="${index}">−</button>
                    <input type="text" class="form-control text-center" value="${item.qty}" readonly>
                    <button class="btn btn-outline-secondary btn-plus" data-index="${index}">+</button>
                </div>
            </td>
            <td class="text-end">${formatPrice(item.price)}</td>
            <td class="text-end fw-semibold">${formatPrice(item.price * item.qty)}</td>
            <td class="text-end">
                <button class="btn btn-sm btn-outline-danger btn-remove" data-index="${index}">✕</button>
            </td>
        `;

        tbody.appendChild(row);
    });

    attachRowEvents();
    updateTotals();
}

// Обработчики кнопок +/− и удаления
function attachRowEvents() {
    document.querySelectorAll('.btn-plus').forEach(btn => {
        btn.addEventListener('click', () => {
            const i = parseInt(btn.dataset.index);
            cart[i].qty++;
            saveCart();
            renderCart();
        });
    });

    document.querySelectorAll('.btn-minus').forEach(btn => {
        btn.addEventListener('click', () => {
            const i = parseInt(btn.dataset.index);
            if (cart[i].qty > 1) {
                cart[i].qty--;
            } else {
                cart.splice(i, 1); // если 1 → удаляем
            }
            saveCart();
            renderCart();
        });
    });

    document.querySelectorAll('.btn-remove').forEach(btn => {
        btn.addEventListener('click', () => {
            const i = parseInt(btn.dataset.index);
            cart.splice(i, 1);
            saveCart();
            renderCart();
        });
    });
}

// Подсчёт итогов
function updateTotals() {
    const totalItems = cart.reduce((sum, i) => sum + i.qty, 0);
    const subtotal = cart.reduce((sum, i) => sum + i.price * i.qty, 0);

    document.getElementById('totalItems').textContent = totalItems;
    document.getElementById('subtotal').textContent = formatPrice(subtotal);
    document.getElementById('grandTotal').textContent = formatPrice(subtotal);
}

// Оформление заказа
document.getElementById('checkoutBtn').addEventListener('click', async () => {
    if (cart.length === 0) return;

    const lines = cart.map(i => ({ id: parseInt(i.id), qty: i.qty }));

    const token = document.querySelector('input[name="__RequestVerificationToken"]').value;

    const response = await fetch('/Orders/Create', {
        method: 'POST',
        headers: {
            'Content-Type': 'application/json',
            'RequestVerificationToken': token
        },
        body: JSON.stringify(lines)
    });

    if (response.status === 401) {
        window.location.href = '/Account/Login?returnUrl=/Cart';
        return;
    }
    if (!response.ok) {
        alert('Не удалось оформить заказ');
        return;
    }

    const result = await response.json();
    alert('Заказ #' + result.orderId + ' оформлен!');
    cart = [];
    saveCart();
    renderCart();
});

// Старт
renderCart();