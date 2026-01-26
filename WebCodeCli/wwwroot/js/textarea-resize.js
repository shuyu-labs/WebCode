// Textarea resize functionality with localStorage caching
window.textareaResizeManager = {
    // Configuration
    config: {
        minHeight: 72,  // Minimum height: 3 rows of text (approximately 24px per row)
        maxHeight: 400, // Maximum height limit
        storageKey: 'textarea-heights',
        heightTolerance: 1 // Pixel tolerance for height comparison to avoid floating point issues
    },

    // Initialize textarea resize functionality
    init: function() {
        // Restore saved heights on page load
        this.restoreHeights();
        
        // Setup observers for all textareas
        this.observeTextareas();
        
        console.log('Textarea resize manager initialized');
    },

    // Get saved heights from localStorage
    getSavedHeights: function() {
        try {
            const saved = localStorage.getItem(this.config.storageKey);
            return saved ? JSON.parse(saved) : {};
        } catch (e) {
            console.error('Failed to load saved textarea heights:', e);
            return {};
        }
    },

    // Save height to localStorage
    saveHeight: function(textareaId, height) {
        try {
            const heights = this.getSavedHeights();
            heights[textareaId] = height;
            localStorage.setItem(this.config.storageKey, JSON.stringify(heights));
        } catch (e) {
            console.error('Failed to save textarea height:', e);
        }
    },

    // Apply constraints to height
    constrainHeight: function(height) {
        return Math.max(this.config.minHeight, Math.min(height, this.config.maxHeight));
    },

    // Restore saved heights to textareas
    restoreHeights: function() {
        const savedHeights = this.getSavedHeights();
        
        Object.keys(savedHeights).forEach(textareaId => {
            const textarea = document.getElementById(textareaId);
            if (textarea) {
                const height = this.constrainHeight(savedHeights[textareaId]);
                textarea.style.height = height + 'px';
            }
        });
    },

    // Setup ResizeObserver for a textarea
    setupResizeObserver: function(textarea) {
        const textareaId = textarea.id;
        if (!textareaId) return;

        // Apply initial constraints
        const currentHeight = textarea.offsetHeight;
        if (currentHeight) {
            const constrainedHeight = this.constrainHeight(currentHeight);
            if (currentHeight !== constrainedHeight) {
                textarea.style.height = constrainedHeight + 'px';
            }
        }

        // Create ResizeObserver to watch for size changes
        const resizeObserver = new ResizeObserver(entries => {
            for (let entry of entries) {
                const target = entry.target;
                const newHeight = entry.contentRect.height;
                
                // Apply constraints and save
                const constrainedHeight = this.constrainHeight(newHeight);
                
                // Only update if height changed and needs constraining
                if (Math.abs(newHeight - constrainedHeight) > this.config.heightTolerance) {
                    target.style.height = constrainedHeight + 'px';
                }
                
                // Save to localStorage (with debounce)
                this.debouncedSave(textareaId, constrainedHeight);
            }
        });

        resizeObserver.observe(textarea);
        
        // Store observer reference for cleanup if needed
        textarea._resizeObserver = resizeObserver;
    },

    // Debounced save function
    debouncedSave: (function() {
        let timeouts = {};
        const manager = this;
        return function(textareaId, height) {
            if (timeouts[textareaId]) {
                clearTimeout(timeouts[textareaId]);
            }
            timeouts[textareaId] = setTimeout(() => {
                window.textareaResizeManager.saveHeight(textareaId, height);
            }, 500); // Save 500ms after resize stops
        };
    })(),

    // Observe all textareas with resize enabled
    observeTextareas: function() {
        // Wait for DOM to be ready
        const checkAndSetup = () => {
            const textareas = document.querySelectorAll('textarea[id]');
            textareas.forEach(textarea => {
                const computedStyle = window.getComputedStyle(textarea);
                const resize = computedStyle.resize;
                
                // Only setup for resizable textareas
                if (resize !== 'none' && !textarea._resizeObserver) {
                    this.setupResizeObserver(textarea);
                }
            });
        };

        // Initial setup
        checkAndSetup();

        // Setup MutationObserver to watch for dynamically added textareas
        const observer = new MutationObserver(() => {
            checkAndSetup();
        });

        observer.observe(document.body, {
            childList: true,
            subtree: true
        });
    }
};

// Initialize when DOM is ready
if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => {
        window.textareaResizeManager.init();
    });
} else {
    // DOM is already ready
    window.textareaResizeManager.init();
}

// Also re-initialize after Blazor renders
if (window.Blazor) {
    window.Blazor.addEventListener('enhancedload', () => {
        window.textareaResizeManager.restoreHeights();
    });
}

console.log('Textarea resize script loaded');
