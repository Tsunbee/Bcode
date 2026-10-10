<%@ Page AutoEventWireup="false" MasterPageFile="~/Main/MasterPage.master" Inherits="FastBusiness.ReportExtender.UI.Page" v="Danh mục khế ước" e="Loan Contract List"%>

<asp:Content ID="headContent" ContentPlaceHolderID="head" runat="server"></asp:Content>
<asp:Content ID="mainContent" ContentPlaceHolderID="FastBusiness" runat="server">
		<div>
				<asp:Panel ID="panelReport" runat="server"/>
		</div>
		<FastBusiness:ReportExtender ID="MainReport" runat="server" TargetControlID="panelReport" ReadOnly="true"  FilterMode="true" Controller="LoanContract"  InitScript=""/>
		<div id="toast-container" style="position: fixed; top: 20px; right: 20px; z-index: 99999;"></div>
		<script type="text/javascript">
(function () {

	var style = document.createElement('style');
	style.textContent = `
		.toast {
			background: #333;
			color: #fff;
			padding: 10px 15px;
			margin-top: 10px;
			border-radius: 4px;
			opacity: 0.95;
			transition: opacity 0.5s ease-out;
			position: relative;
			font-family: sans-serif;
			min-width: 200px;
			white-space: pre-line;
			line-height: 1;
		}
		.toast.dragging {
			opacity: 1;
			transition: none;
			z-index: 99999;
			cursor: grabbing;
		}
		.toast .close {
			position: absolute;
			top: 5px;
			right: 8px;
			cursor: pointer;
			font-size: 14px;
		}
		.toast .Highlight {
			color: #fff;
			background-color: #ffc107;
			padding: 0 4px;
			border-radius: 2px;
			display: inline-block;
			line-height: 1.5;
		}
	`;
	document.head.appendChild(style);
	
	
	window.$toast = function (options = {}) {
		this.message = options.message || '';
		this.duration = options.duration || 3000;
		this.sticky = options.sticky || false; 


		this.show = function () {
			var container = document.getElementById('toast-container');
			if (!container) return;

			var toast = document.createElement('div');
			toast.className = 'toast';
			toast.innerHTML = `
				${this.message}
				<span class="close" onclick="this.parentElement.style.opacity='0'; setTimeout(() => this.parentElement.remove(), 500);">&times;</span>
			`;

			container.appendChild(toast);
			
			var isDragging = false;
			var offsetX = 0, offsetY = 0; 
			
			toast.addEventListener('mousedown', function (e) {
				if (e.target.classList.contains('close')) return;

				isDragging = true;
				toast.classList.add('dragging');
				toast.style.zIndex = '100001'

				const rect = toast.getBoundingClientRect();
				offsetX = e.clientX - rect.left;
				offsetY = e.clientY - rect.top;

				toast.style.position = 'absolute';
				toast.style.left = `${rect.left}px`;
				toast.style.top = `${rect.top}px`;

				
				if (toast.parentElement !== document.body) {
					document.body.appendChild(toast);
				}
			});
			
			document.addEventListener('mousemove', function (e) {
				if (!isDragging) return;
				toast.style.left = `${e.clientX - offsetX}px`;
				toast.style.top = `${e.clientY - offsetY}px`;
			});
			
			document.addEventListener('mouseup', function () {
				if (!isDragging) return;
				isDragging = false;
				toast.classList.remove('dragging');
			});

			if (!this.sticky) {
				setTimeout(() => {
					if (!toast.classList.contains('dragging')) {
						toast.style.opacity = '0';
						setTimeout(() => toast.remove(), 500);
					}
				}, this.duration);
			}
			
		};
	};

})();
</script>
</asp:Content>	