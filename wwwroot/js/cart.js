// Берём корзину из localStorage (её туда пишет script.js с главной)
window.cart = JSON.parse(localStorage.getItem('cart') || '[]')
    .filter(item => Number.isInteger(Number(item.variantId)) && Number(item.qty) > 0);
localStorage.setItem('cart', JSON.stringify(window.cart));

const tbody = document.getElementById('cartTableBody');
const emptyBlock = document.getElementById('emptyCart');
const contentBlock = document.getElementById('cartContent');
const currencyContainer = document.querySelector('[data-currency-code]');
const currencyFormatter = new Intl.NumberFormat(currencyContainer.dataset.currencyCulture, {
    style: 'currency',
    currency: currencyContainer.dataset.currencyCode,
    maximumFractionDigits: 0
});

// Сохранение и обновление счётчика в шапке
function saveCart() {
    localStorage.setItem('cart', JSON.stringify(window.cart));
    updateCartBadge(); // функция из script.js
}

// Форматирование цены
function formatPrice(value) {
    return currencyFormatter.format(value);
}

// Рендер всей корзины
function renderCart() {
    if (window.cart.length === 0) {
        emptyBlock.classList.remove('d-none');
        contentBlock.classList.add('d-none');
        return;
    }
    emptyBlock.classList.add('d-none');
    contentBlock.classList.remove('d-none');

    tbody.innerHTML = '';

    window.cart.forEach((item, index) => {
        const row = document.createElement('tr');

        row.innerHTML = `
            <td>
                <div class="d-flex align-items-center gap-3">
                    <img src="https://placehold.co/60x60" alt="" width="60" height="60" class="rounded">
                    <span class="fw-semibold">${escapeHtml(item.name)}<br>
                        <small class="text-muted">${escapeHtml(item.size)} · ${escapeHtml(item.color)}</small>
                    </span>
                </div>
            </td>
            <td class="text-center">
                <div class="input-group input-group-sm justify-content-center" style="max-width:120px; margin:auto;">
                    <button class="btn btn-outline-secondary btn-minus" data-index="${index}">−</button>
                    <input type="text" class="form-control text-center" value="${item.qty}" readonly>
                    <button class="btn btn-outline-secondary btn-plus" data-index="${index}"
                            ${item.qty >= item.stock ? 'disabled' : ''}>+</button>
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

function escapeHtml(value) {
    return String(value ?? '').replace(/[&<>"']/g, character => ({
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#39;'
    })[character]);
}

// Обработчики кнопок +/− и удаления
function attachRowEvents() {
    document.querySelectorAll('.btn-plus').forEach(btn => {
        btn.addEventListener('click', () => {
            const i = parseInt(btn.dataset.index);
            if (window.cart[i].qty >= window.cart[i].stock) return;
            window.cart[i].qty++;
            saveCart();
            renderCart();
        });
    });

    document.querySelectorAll('.btn-minus').forEach(btn => {
        btn.addEventListener('click', () => {
            const i = parseInt(btn.dataset.index);
            if (window.cart[i].qty > 1) {
                window.cart[i].qty--;
            } else {
                window.cart.splice(i, 1);
            }
            saveCart();
            renderCart();
        });
    });

    document.querySelectorAll('.btn-remove').forEach(btn => {
        btn.addEventListener('click', () => {
            const i = parseInt(btn.dataset.index);
            window.cart.splice(i, 1);
            saveCart();
            renderCart();
        });
    });
}

// Подсчёт итогов
function updateTotals() {
    const totalItems = window.cart.reduce((sum, i) => sum + i.qty, 0);
    const subtotal = window.cart.reduce((sum, i) => sum + i.price * i.qty, 0);

    document.getElementById('totalItems').textContent = totalItems;
    document.getElementById('subtotal').textContent = formatPrice(subtotal);
    document.getElementById('grandTotal').textContent = formatPrice(subtotal);
}

// Оформление заказа
document.getElementById('checkoutBtn').addEventListener('click', async () => {
    if (window.cart.length === 0) return;

    const lines = window.cart.map(item => ({ variantId: Number(item.variantId), qty: item.qty }));

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
        if (response.status === 409) {
            const problem = await response.json();
            alert(problem.message || 'Вариант товара закончился. Обновите корзину и попробуйте снова.');
        } else {
            alert('Не удалось оформить заказ');
        }
        return;
    }

    const result = await response.json();
    alert('Заказ #' + result.orderId + ' оформлен!');
    window.cart = [];
    saveCart();
    renderCart();
});

// Старт
renderCart();