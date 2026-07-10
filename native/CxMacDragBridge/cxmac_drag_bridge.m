#import "cxmac_drag_bridge.h"

#import <AppKit/AppKit.h>
#include <stdio.h>

@interface CxMacPromiseEntry : NSObject

@property(nonatomic, copy) NSString* fileName;
@property(nonatomic, assign) void* context;
@property(nonatomic, assign) BOOL started;
@property(nonatomic, assign) BOOL finished;
@property(nonatomic, assign) BOOL cancelNotified;
@property(nonatomic, assign) BOOL released;

@end

@implementation CxMacPromiseEntry
@end

@interface CxMacPromiseSession : NSObject <NSFilePromiseProviderDelegate, NSDraggingSource>

@property(nonatomic, strong) NSArray<CxMacPromiseEntry*>* entries;
@property(nonatomic, strong) NSOperationQueue* operationQueue;
@property(nonatomic, assign) CxMacWritePromiseCallback writeCallback;
@property(nonatomic, assign) CxMacPromiseCallback cancelCallback;
@property(nonatomic, assign) CxMacPromiseCallback releaseCallback;
@property(nonatomic, assign) BOOL dragEnded;

- (void)releaseEntry:(CxMacPromiseEntry*)entry;
- (void)removeIfFinished;

@end

static NSMutableSet<CxMacPromiseSession*>* CxMacActiveSessions(void)
{
    static NSMutableSet<CxMacPromiseSession*>* sessions;
    static dispatch_once_t onceToken;
    dispatch_once(&onceToken, ^{
        sessions = [[NSMutableSet alloc] init];
    });
    return sessions;
}

static void CxMacSetError(char* buffer, int bufferSize, NSString* message)
{
    if (buffer == NULL || bufferSize <= 0)
        return;

    const char* text = message.UTF8String ?: "Unknown macOS drag error.";
    snprintf(buffer, (size_t)bufferSize, "%s", text);
}

@implementation CxMacPromiseSession

- (NSString*)filePromiseProvider:(NSFilePromiseProvider*)filePromiseProvider
                 fileNameForType:(NSString*)fileType
{
    CxMacPromiseEntry* entry = (CxMacPromiseEntry*)filePromiseProvider.userInfo;
    return entry.fileName;
}

- (NSOperationQueue*)operationQueueForFilePromiseProvider:(NSFilePromiseProvider*)filePromiseProvider
{
    return self.operationQueue;
}

- (void)filePromiseProvider:(NSFilePromiseProvider*)filePromiseProvider
          writePromiseToURL:(NSURL*)url
          completionHandler:(void (^)(NSError* errorOrNil))completionHandler
{
    CxMacPromiseEntry* entry = (CxMacPromiseEntry*)filePromiseProvider.userInfo;
    @synchronized (entry)
    {
        entry.started = YES;
    }

    NSURL* destinationURL = [url URLByAppendingPathComponent:entry.fileName isDirectory:NO];
    int result = -1;
    if (self.writeCallback != NULL)
        result = self.writeCallback(entry.context, destinationURL.fileSystemRepresentation);

    NSError* error = nil;
    if (result != 0)
    {
        error = [NSError errorWithDomain:@"com.cxshell.file-promise"
                                    code:result
                                userInfo:@{
                                    NSLocalizedDescriptionKey: @"CxShell could not download the promised file."
                                }];
    }

    @synchronized (entry)
    {
        entry.finished = YES;
    }

    completionHandler(error);
    [self releaseEntry:entry];
}

- (NSDragOperation)draggingSession:(NSDraggingSession*)session
    sourceOperationMaskForDraggingContext:(NSDraggingContext)context
{
    return NSDragOperationCopy;
}

- (BOOL)ignoreModifierKeysForDraggingSession:(NSDraggingSession*)session
{
    return YES;
}

- (void)draggingSession:(NSDraggingSession*)session
            endedAtPoint:(NSPoint)screenPoint
               operation:(NSDragOperation)operation
{
    self.dragEnded = YES;
    if (operation != NSDragOperationNone)
    {
        [self removeIfFinished];
        return;
    }

    for (CxMacPromiseEntry* entry in self.entries)
    {
        BOOL shouldNotify = NO;
        BOOL shouldRelease = NO;
        @synchronized (entry)
        {
            if (!entry.finished && !entry.cancelNotified)
            {
                entry.cancelNotified = YES;
                shouldNotify = YES;
            }
            shouldRelease = !entry.started;
        }

        if (shouldNotify && self.cancelCallback != NULL)
            self.cancelCallback(entry.context);
        if (shouldRelease)
            [self releaseEntry:entry];
    }
}

- (void)releaseEntry:(CxMacPromiseEntry*)entry
{
    BOOL shouldRelease = NO;
    @synchronized (entry)
    {
        if (!entry.released)
        {
            entry.released = YES;
            shouldRelease = YES;
        }
    }

    if (shouldRelease && self.releaseCallback != NULL)
        self.releaseCallback(entry.context);

    [self removeIfFinished];
}

- (void)removeIfFinished
{
    if (!self.dragEnded)
        return;

    for (CxMacPromiseEntry* entry in self.entries)
    {
        @synchronized (entry)
        {
            if (!entry.released)
                return;
        }
    }

    NSMutableSet<CxMacPromiseSession*>* sessions = CxMacActiveSessions();
    @synchronized (sessions)
    {
        [sessions removeObject:self];
    }
}

@end

int cxmac_drag_bridge_version(void)
{
    return 1;
}

int cxmac_begin_file_promise_drag(
    void* nativeWindowOrView,
    const CxMacFilePromiseDescriptor* descriptors,
    int descriptorCount,
    CxMacWritePromiseCallback writeCallback,
    CxMacPromiseCallback cancelCallback,
    CxMacPromiseCallback releaseCallback,
    char* errorBuffer,
    int errorBufferSize)
{
    @autoreleasepool
    {
        CxMacPromiseSession* owner = nil;
        if (nativeWindowOrView == NULL || descriptors == NULL || descriptorCount <= 0)
        {
            CxMacSetError(errorBuffer, errorBufferSize, @"The macOS drag source is unavailable.");
            return 0;
        }

        if (![NSThread isMainThread])
        {
            CxMacSetError(errorBuffer, errorBufferSize, @"The macOS drag session must start on the main thread.");
            return 0;
        }

        @try
        {
            id nativeObject = (__bridge id)nativeWindowOrView;
            NSView* sourceView = nil;
            if ([nativeObject isKindOfClass:[NSWindow class]])
                sourceView = [(NSWindow*)nativeObject contentView];
            else if ([nativeObject isKindOfClass:[NSView class]])
                sourceView = (NSView*)nativeObject;

            NSEvent* event = NSApp.currentEvent;
            if (sourceView == nil || event == nil)
            {
                CxMacSetError(errorBuffer, errorBufferSize, @"The current Finder drag event is unavailable.");
                return 0;
            }

            owner = [[CxMacPromiseSession alloc] init];
            owner.writeCallback = writeCallback;
            owner.cancelCallback = cancelCallback;
            owner.releaseCallback = releaseCallback;
            owner.operationQueue = [[NSOperationQueue alloc] init];
            owner.operationQueue.name = @"CxShell Finder file promises";
            owner.operationQueue.maxConcurrentOperationCount = 2;

            NSMutableArray<CxMacPromiseEntry*>* entries = [[NSMutableArray alloc] initWithCapacity:(NSUInteger)descriptorCount];
            NSMutableArray<NSDraggingItem*>* draggingItems = [[NSMutableArray alloc] initWithCapacity:(NSUInteger)descriptorCount];
            NSPoint location = [sourceView convertPoint:event.locationInWindow fromView:nil];

            for (int index = 0; index < descriptorCount; ++index)
            {
                NSString* fileName = [NSString stringWithUTF8String:descriptors[index].file_name_utf8 ?: "download"];
                if (fileName.length == 0)
                    fileName = @"download";

                CxMacPromiseEntry* entry = [[CxMacPromiseEntry alloc] init];
                entry.fileName = fileName;
                entry.context = descriptors[index].context;
                [entries addObject:entry];

                NSFilePromiseProvider* provider = [[NSFilePromiseProvider alloc]
                    initWithFileType:@"public.data"
                    delegate:owner];
                provider.userInfo = entry;

                NSDraggingItem* draggingItem = [[NSDraggingItem alloc] initWithPasteboardWriter:provider];
                NSImage* icon = [[[NSWorkspace sharedWorkspace] iconForFileType:fileName.pathExtension] copy];
                if (icon == nil)
                    icon = [[NSImage imageNamed:NSImageNameMultipleDocuments] copy];
                icon.size = NSMakeSize(32.0, 32.0);
                NSRect frame = NSMakeRect(location.x - 16.0, location.y - 16.0, 32.0, 32.0);
                [draggingItem setDraggingFrame:frame contents:icon];
                [draggingItems addObject:draggingItem];
            }

            owner.entries = entries;
            NSMutableSet<CxMacPromiseSession*>* sessions = CxMacActiveSessions();
            @synchronized (sessions)
            {
                [sessions addObject:owner];
            }

            NSDraggingSession* session = [sourceView beginDraggingSessionWithItems:draggingItems
                                                                             event:event
                                                                            source:owner];
            session.animatesToStartingPositionsOnCancelOrFail = YES;
            return 1;
        }
        @catch (NSException* exception)
        {
            if (owner != nil)
            {
                NSMutableSet<CxMacPromiseSession*>* sessions = CxMacActiveSessions();
                @synchronized (sessions)
                {
                    [sessions removeObject:owner];
                }
            }
            CxMacSetError(errorBuffer, errorBufferSize, exception.reason ?: @"macOS could not start the Finder drag session.");
            return 0;
        }
    }
}
